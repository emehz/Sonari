using OndaMix.Audio.Core.Buffers;
using OndaMix.Audio.Core.Formats;
using OndaMix.Audio.Engine.Dsp;

namespace OndaMix.Audio.Engine.Routing;

/// <summary>
/// Una fuente dentro del motor: el hilo de captura escribe en el ring buffer (<see cref="OnCaptured"/>) y el hilo de
/// render del bus lee, aplica fader y envío y suma en la mezcla (<see cref="MixInto"/>).
/// En la Fase 4 cada canal tendrá un lector por bus; ahora solo existe el de monitoreo.
/// </summary>
internal sealed class MixerChannel
{
    /// <summary>Tamaño máximo de bloque interno de procesamiento (≈ 43 ms a 48 kHz).</summary>
    public const int MaxBlockFrames = 2048;

    private readonly ChannelControl _control;
    private readonly int _channels;
    private readonly SmoothedGain _fader = new(0f);
    private readonly SmoothedGain _monitorSend = new(0f);
    private long _overrunSamples;
    private long _discontinuities;

    public MixerChannel(ChannelControl control, AudioFormat format, int targetFrames)
    {
        _control = control;
        _channels = format.Channels;

        // Cabe el umbral de resync del lector (4 × objetivo) más un bloque, y como mínimo medio segundo.
        int capacityFrames = Math.Max(format.SampleRate / 2, (targetFrames * 4) + MaxBlockFrames);
        Ring = new SpscRingBuffer<float>(capacityFrames * _channels);
        MonitorReader = new DriftCompensatingReader(Ring, _channels, format.SampleRate, MaxBlockFrames, targetFrames);
    }

    public SpscRingBuffer<float> Ring { get; }

    public DriftCompensatingReader MonitorReader { get; }

    public PeakMeter InputMeter { get; } = new();

    public PeakMeter PostFaderMeter { get; } = new();

    public long OverrunSamples => Interlocked.Read(ref _overrunSamples);

    public long Discontinuities => Interlocked.Read(ref _discontinuities);

    /// <summary>Hilo de captura.</summary>
    public void OnCaptured(ReadOnlySpan<float> interleaved, bool discontinuity)
    {
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

    /// <summary>Solo con los dos streams parados (ningún hilo toca el ring buffer).</summary>
    public void ResetForRestart()
    {
        Ring.DiscardAll();
        MonitorReader.ResetToPriming();
        _fader.Target = _control.FaderLinear;
        _fader.Snap();
        _monitorSend.Target = _control.SendToMonitor ? 1f : 0f;
        _monitorSend.Snap();
    }
}
