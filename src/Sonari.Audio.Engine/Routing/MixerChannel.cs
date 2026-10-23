using Sonari.Audio.Core.Buffers;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Engine.Dsp;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Una fuente dentro del motor: el hilo de captura escribe en el ring buffer (<see cref="OnCaptured"/>) y el hilo de
/// render del bus lee, aplica fader y envío y suma en la mezcla (<see cref="MixInto"/>).
/// En la Fase 4 cada canal tendrá un lector por bus; ahora solo existe el de monitoreo.
/// </summary>
internal sealed class MixerChannel
{
    /// <summary>Tamaño máximo de bloque interno de procesamiento (≈ 43 ms a 48 kHz).</summary>
    public const int MaxBlockFrames = 2048;

    // Si una fuente intermitente vuelve a enviar en este plazo tras un vaciado, fue un corte real; si no, se había callado.
    private const long IdleDecisionMilliseconds = 100;

    private readonly ChannelControl _control;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly bool _isIntermittent;
    private readonly SmoothedGain _fader = new(0f);
    private readonly SmoothedGain _monitorSend = new(0f);
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
    {
        _control = control;
        _channels = format.Channels;
        _sampleRate = format.SampleRate;
        _isIntermittent = isIntermittent;

        // Cabe el umbral de resync del lector (4 × objetivo) más un bloque, y como mínimo medio segundo.
        int capacityFrames = Math.Max(format.SampleRate / 2, (targetFrames * 4) + MaxBlockFrames);
        Ring = new SpscRingBuffer<float>(capacityFrames * _channels);
        MonitorReader = new DriftCompensatingReader(Ring, _channels, format.SampleRate, MaxBlockFrames, targetFrames);
    }

    public SpscRingBuffer<float> Ring { get; }

    public DriftCompensatingReader MonitorReader { get; }

    public PeakMeter InputMeter { get; } = new();

    public PeakMeter PostFaderMeter { get; } = new();

    /// <summary>Hilo de captura.</summary>
    public void OnCaptured(ReadOnlySpan<float> interleaved, bool discontinuity)
    {
        Volatile.Write(ref _lastPacketTicks, Environment.TickCount64);
        if (discontinuity)
        {
            Interlocked.Increment(ref _discontinuities);
        }

        InputMeter.Process(interleaved, _channels);

        int written = Ring.Write(interleaved);
        if (written < interleaved.Length)
        {
            Interlocked.Add(ref _overrunSamples, interleaved.Length - written);
        }
    }

    /// <summary>Hilo de render. <paramref name="scratch"/> debe tener al menos el tamaño de <paramref name="mix"/>.</summary>
    public void MixInto(Span<float> mix, Span<float> scratch)
    {
        Span<float> buffer = scratch[..mix.Length];

        // Se lee siempre, aunque el envío esté a cero: si no, el buffer se llenaría y la deriva no se compensaría.
        MonitorReader.Read(buffer);
        ClassifyUnderruns();

        _fader.Target = _control.FaderLinear;
        _fader.Apply(buffer, _channels);
        PostFaderMeter.Process(buffer, _channels);

        _monitorSend.Target = _control.SendToMonitor ? 1f : 0f;
        if (_monitorSend.Current == 0f && _monitorSend.Target == 0f)
        {
            return;
        }

        _monitorSend.Apply(buffer, _channels);
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] += buffer[i];
        }
    }

    public ChannelMetrics ReadMetrics()
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
    public void ResetForRestart()
    {
        Ring.DiscardAll();
        MonitorReader.ResetToPriming();
        _seenReaderUnderruns = MonitorReader.Underruns;
        _pendingUnderruns = 0;
        _fader.Target = _control.FaderLinear;
        _fader.Snap();
        _monitorSend.Target = _control.SendToMonitor ? 1f : 0f;
        _monitorSend.Snap();
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
