using Sonari.Audio.Core.Buffers;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Canal alimentado por una captura (micrófono, aplicación o sistema): el hilo de captura escribe en el ring buffer
/// (<see cref="OnCaptured"/>) y el hilo de render lo lee compensando la deriva entre los dos relojes.
/// En la Fase 5 cada canal tendrá un lector por bus; ahora solo existe el de monitoreo.
/// </summary>
internal sealed class MixerChannel : ChannelStrip
{
    /// <summary>Tamaño máximo de bloque interno de procesamiento (≈ 43 ms a 48 kHz).</summary>
    public const int MaxBlockFrames = 2048;

    // Si una fuente intermitente vuelve a enviar en este plazo tras un vaciado, fue un corte real; si no, se había callado.
    private const long IdleDecisionMilliseconds = 100;

    private readonly int _sampleRate;
    private readonly bool _isIntermittent;
    private long _overrunSamples;
    private long _discontinuities;
    private long _underruns;
    private long _idleGaps;
    private long _lastPacketTicks;

    // Solo los usa el hilo de render.
    private long _seenReaderUnderruns;
    private long _pendingUnderruns;
    private long _pendingSinceTicks;

    /// <param name="isIntermittent">
    /// La fuente puede dejar de enviar paquetes sin que sea un error (una aplicación que cierra su stream de audio).
    /// Sus vaciados se clasifican como cortes reales o como pausas de la aplicación.
    /// </param>
    public MixerChannel(ChannelControl control, AudioFormat format, int targetFrames, bool isIntermittent)
        : base(control, format.Channels)
    {
        _sampleRate = format.SampleRate;
        _isIntermittent = isIntermittent;

        // Cabe el umbral de resync del lector (4 × objetivo) más un bloque, y como mínimo medio segundo.
        int capacityFrames = Math.Max(format.SampleRate / 2, (targetFrames * 4) + MaxBlockFrames);
        Ring = new SpscRingBuffer<float>(capacityFrames * format.Channels);
        MonitorReader = new DriftCompensatingReader(Ring, format.Channels, format.SampleRate, MaxBlockFrames, targetFrames);
    }

    public SpscRingBuffer<float> Ring { get; }

    public DriftCompensatingReader MonitorReader { get; }

    /// <summary>Hilo de captura.</summary>
    public void OnCaptured(ReadOnlySpan<float> interleaved, bool discontinuity)
    {
        Volatile.Write(ref _lastPacketTicks, Environment.TickCount64);
        if (discontinuity)
        {
            Interlocked.Increment(ref _discontinuities);
        }

        InputMeter.Process(interleaved, Channels);

        int written = Ring.Write(interleaved);
        if (written < interleaved.Length)
        {
            Interlocked.Add(ref _overrunSamples, interleaved.Length - written);
        }
    }

    public override ChannelMetrics ReadMetrics()
    {
        DriftCompensatingReader reader = MonitorReader;
        return new ChannelMetrics(
            Input: InputMeter.ReadAndReset(),
            PostFader: PostFaderMeter.ReadAndReset(),
            BufferMilliseconds: reader.IsPriming ? 0 : reader.SmoothedFillFrames * 1000.0 / _sampleRate,
            DriftPpm: reader.CorrectionPpm,
            Underruns: Interlocked.Read(ref _underruns),
            IdleGaps: Interlocked.Read(ref _idleGaps),
            OverrunSamples: Interlocked.Read(ref _overrunSamples),
            Discontinuities: Interlocked.Read(ref _discontinuities));
    }

    /// <summary>Solo con la salida parada. La captura puede seguir escribiendo: vaciar el ring es una operación del consumidor.</summary>
    public override void ResetForRestart()
    {
        Ring.DiscardAll();
        MonitorReader.ResetToPriming();
        _seenReaderUnderruns = MonitorReader.Underruns;
        _pendingUnderruns = 0;
        base.ResetForRestart();
    }

    protected override void ReadInput(Span<float> buffer)
    {
        MonitorReader.Read(buffer);
        ClassifyUnderruns();
    }

    private void ClassifyUnderruns()
    {
        long readerUnderruns = MonitorReader.Underruns;
        long fresh = readerUnderruns - _seenReaderUnderruns;
        _seenReaderUnderruns = readerUnderruns;

        if (!_isIntermittent)
        {
            if (fresh > 0)
            {
                Interlocked.Add(ref _underruns, fresh);
            }

            return;
        }

        long now = Environment.TickCount64;
        if (fresh > 0 && _pendingUnderruns == 0)
        {
            _pendingSinceTicks = now;
        }

        _pendingUnderruns += fresh;
        if (_pendingUnderruns == 0)
        {
            return;
        }

        if (Volatile.Read(ref _lastPacketTicks) > _pendingSinceTicks)
        {
            // Volvieron a llegar paquetes enseguida: el buffer se vació con la fuente activa.
            Interlocked.Add(ref _underruns, _pendingUnderruns);
            _pendingUnderruns = 0;
        }
        else if (now - _pendingSinceTicks > IdleDecisionMilliseconds)
        {
            Interlocked.Add(ref _idleGaps, _pendingUnderruns);
            _pendingUnderruns = 0;
        }
    }
}
