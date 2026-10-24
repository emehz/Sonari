using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Engine.Dsp;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Reparte el audio de una fuente a cada mezcla. Cada <see cref="BusInput"/> tiene su propio ring buffer porque cada
/// mezcla la consume un hilo distinto, al ritmo de su propio dispositivo.
/// <list type="bullet">
/// <item><b>Fuentes capturadas</b> (micrófono, aplicaciones, sistema): el hilo de captura escribe en todos los rings activos.</item>
/// <item><b>Fuentes generadas</b> (reproductor de sonidos): no tienen reloj propio. Las genera la mezcla «principal»
/// (la del micrófono virtual si está activa, si no la de monitoreo) dentro de su bloque, que reenvía una copia a la otra.
/// Así, si los auriculares fallan, los demás siguen oyendo los sonidos.</item>
/// </list>
/// El micrófono pasa además por la cadena de voz (<see cref="VoiceProcessor"/>) antes de repartirse: se procesa una
/// sola vez y las dos mezclas reciben la misma voz.
/// Se crea en cada arranque del motor (los buffers dependen del formato).
/// </summary>
internal sealed class SourceFeed
{
    /// <summary>Tamaño máximo de bloque interno de procesamiento (≈ 43 ms a 48 kHz).</summary>
    public const int MaxBlockFrames = 2048;

    private readonly BusInput[] _inputs;
    private readonly RenderHandler? _generator;
    private readonly VoiceProcessor? _voice;
    private readonly float[]? _voiceBuffer;
    private readonly int _channels;
    private int _primaryBus;
    private int _generating;
    private long _overrunSamples;
    private long _discontinuities;
    private long _lastPacketTicks;
    private long _skippedGenerations;

    public SourceFeed(ChannelControl control, AudioFormat format, int targetFrames, SourceKind kind, bool withVirtualBus,
        RenderHandler? generator, VoiceProcessor? voice = null)
    {
        _channels = format.Channels;
        _generator = generator;
        _voice = voice;
        _voiceBuffer = voice is null ? null : new float[MaxBlockFrames * format.Channels];
        bool intermittent = kind is SourceKind.Application or SourceKind.SystemAudio;

        BusInput Create(BusKind bus) => new(this, control, bus, format, targetFrames, intermittent);
        _inputs = withVirtualBus ? [Create(BusKind.Monitor), Create(BusKind.Virtual)] : [Create(BusKind.Monitor)];
    }

    public BusInput MonitorInput => _inputs[0];

    public BusInput? VirtualInput => _inputs.Length > 1 ? _inputs[1] : null;

    public bool IsGenerator => _generator is not null;

    /// <summary>Mezcla que genera el audio de una fuente generada. Solo lo cambia el motor.</summary>
    public BusKind PrimaryBus
    {
        get => (BusKind)Volatile.Read(ref _primaryBus);
        set => Volatile.Write(ref _primaryBus, (int)value);
    }

    public PeakMeter InputMeter { get; } = new();

    public long LastPacketTicks => Volatile.Read(ref _lastPacketTicks);

    /// <summary>Bloques generados en silencio porque dos mezclas coincidieron durante un cambio de mezcla principal.</summary>
    public long SkippedGenerations => Interlocked.Read(ref _skippedGenerations);

    /// <summary>Hilo de captura: escribe en el ring de cada mezcla activa.</summary>
    public void OnCaptured(ReadOnlySpan<float> interleaved, bool discontinuity)
    {
        Volatile.Write(ref _lastPacketTicks, Environment.TickCount64);
        if (discontinuity)
        {
            Interlocked.Increment(ref _discontinuities);
        }

        // El medidor de entrada mide la voz antes de los efectos: es lo que hace falta para ajustar la puerta de ruido.
        InputMeter.Process(interleaved, _channels);
        if (_voice is null)
        {
            WriteToInputs(interleaved, except: null);
            return;
        }

        // El paquete de captura es de solo lectura: se procesa una copia, por trozos del tamaño del buffer reservado.
        float[] buffer = _voiceBuffer!;
        int whole = interleaved.Length - (interleaved.Length % _channels);
        for (int offset = 0; offset < whole; offset += buffer.Length)
        {
            Span<float> block = buffer.AsSpan(0, Math.Min(buffer.Length, whole - offset));
            interleaved.Slice(offset, block.Length).CopyTo(block);
            _voice.Process(block);
            WriteToInputs(block, except: null);
        }
    }

    /// <summary>
    /// Hilo de render de la mezcla principal: genera el bloque y reenvía una copia a las demás mezclas.
    /// Si otra mezcla está generando en ese instante (solo durante un cambio de principal), devuelve silencio: el
    /// reproductor nunca se ejecuta en dos hilos a la vez.
    /// </summary>
    public void Generate(BusInput primary, Span<float> buffer)
    {
        if (Interlocked.Exchange(ref _generating, 1) != 0)
        {
            buffer.Clear();
            Interlocked.Increment(ref _skippedGenerations);
            return;
        }

        try
        {
            _generator!(buffer);
        }
        finally
        {
            Volatile.Write(ref _generating, 0);
        }

        Volatile.Write(ref _lastPacketTicks, Environment.TickCount64);
        InputMeter.Process(buffer, _channels);
        WriteToInputs(buffer, except: primary);
    }

    public ChannelMetrics ReadMetrics()
    {
        // Se muestra la mezcla del micrófono virtual si existe: es lo que oyen los demás.
        BusInput shown = VirtualInput is { IsActive: true } virtualInput ? virtualInput : MonitorInput;
        bool generatedHere = IsGenerator && PrimaryBus == shown.Bus;
        DriftCompensatingReader reader = shown.Reader;

        long underruns = 0;
        long idle = 0;
        foreach (BusInput input in _inputs)
        {
            underruns += input.Underruns;
            idle += input.IdleGaps;
        }

        return new ChannelMetrics(
            Input: InputMeter.ReadAndReset(),
            PostFader: shown.PostFaderMeter.ReadAndReset(),
            BufferMilliseconds: generatedHere || reader.IsPriming ? 0 : reader.SmoothedFillFrames * 1000.0 / shown.SampleRate,
            DriftPpm: generatedHere ? 0 : reader.CorrectionPpm,
            Underruns: underruns,
            IdleGaps: idle,
            OverrunSamples: Interlocked.Read(ref _overrunSamples),
            Discontinuities: Interlocked.Read(ref _discontinuities));
    }

    private void WriteToInputs(ReadOnlySpan<float> interleaved, BusInput? except)
    {
        foreach (BusInput input in _inputs)
        {
            // Una mezcla parada no consume: escribirle solo llenaría su buffer y contaría pérdidas falsas.
            if (input == except || !input.IsActive)
            {
                continue;
            }

            int written = input.Ring.Write(interleaved);
            if (written < interleaved.Length)
            {
                Interlocked.Add(ref _overrunSamples, interleaved.Length - written);
            }
        }
    }
}
