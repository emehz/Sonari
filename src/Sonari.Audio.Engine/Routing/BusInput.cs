using Sonari.Audio.Core.Buffers;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Engine.Dsp;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Una fuente dentro de una mezcla concreta, procesada en el hilo de render de esa mezcla: lee su buffer compensando la
/// deriva (o genera, si es la mezcla principal de una fuente generada), aplica fader y balance, mide y suma con el envío
/// de esa mezcla. Fader, balance y silencio son comunes a las dos mezclas; el envío es propio de cada una.
/// </summary>
internal sealed class BusInput
{
    // Si una fuente intermitente vuelve a enviar en este plazo tras un vaciado, fue un corte real; si no, se había callado.
    private const long IdleDecisionMilliseconds = 100;

    private readonly SourceFeed _feed;
    private readonly ChannelControl _control;
    private readonly int _channels;
    private readonly bool _isIntermittent;
    private readonly StereoGain _faderAndBalance = new();
    private readonly SmoothedGain _send = new(0f);
    private int _active;
    private long _underruns;
    private long _idleGaps;

    // Solo los usa el hilo de render de esta mezcla.
    private long _seenReaderUnderruns;
    private long _pendingUnderruns;
    private long _pendingSinceTicks;

    public BusInput(SourceFeed feed, ChannelControl control, BusKind bus, AudioFormat format, int targetFrames, bool isIntermittent)
    {
        _feed = feed;
        _control = control;
        _channels = format.Channels;
        _isIntermittent = isIntermittent;
        Bus = bus;
        SampleRate = format.SampleRate;

        // Cabe el umbral de resync del lector (4 × objetivo) más un bloque, y como mínimo medio segundo.
        int capacityFrames = Math.Max(format.SampleRate / 2, (targetFrames * 4) + SourceFeed.MaxBlockFrames);
        Ring = new SpscRingBuffer<float>(capacityFrames * format.Channels);
        Reader = new DriftCompensatingReader(Ring, format.Channels, format.SampleRate, SourceFeed.MaxBlockFrames, targetFrames);
    }

    public BusKind Bus { get; }

    public int SampleRate { get; }

    public SpscRingBuffer<float> Ring { get; }

    public DriftCompensatingReader Reader { get; }

    public PeakMeter PostFaderMeter { get; } = new();

    /// <summary>La mezcla está sonando: los productores escriben en este buffer solo si está activa.</summary>
    public bool IsActive
    {
        get => Volatile.Read(ref _active) != 0;
        set => Volatile.Write(ref _active, value ? 1 : 0);
    }

    public long Underruns => Interlocked.Read(ref _underruns);

    public long IdleGaps => Interlocked.Read(ref _idleGaps);

    /// <summary>Hilo de render. <paramref name="scratch"/> debe tener al menos el tamaño de <paramref name="mix"/>.</summary>
    public void MixInto(Span<float> mix, Span<float> scratch)
    {
        Span<float> buffer = scratch[..mix.Length];

        if (_feed.IsGenerator && _feed.PrimaryBus == Bus)
        {
            _feed.Generate(this, buffer);
        }
        else
        {
            // Se lee siempre, aunque el envío esté a cero: si no, el buffer se llenaría y la deriva no se compensaría.
            Reader.Read(buffer);
            ClassifyUnderruns();
        }

        (float left, float right) = _control.StereoGains;
        _faderAndBalance.Apply(buffer, _channels, left, right);
        PostFaderMeter.Process(buffer, _channels);

        _send.Target = _control.SendsTo(Bus) ? 1f : 0f;
        if (_send.Current == 0f && _send.Target == 0f)
        {
            return;
        }

        _send.Apply(buffer, _channels);
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] += buffer[i];
        }
    }

    /// <summary>
    /// Solo con la salida de esta mezcla parada. Los productores pueden seguir escribiendo: vaciar el ring es una
    /// operación del consumidor.
    /// </summary>
    public void ResetForRestart()
    {
        Ring.DiscardAll();
        Reader.ResetToPriming();
        _seenReaderUnderruns = Reader.Underruns;
        _pendingUnderruns = 0;
        (float left, float right) = _control.StereoGains;
        _faderAndBalance.Snap(left, right);
        _send.Target = _control.SendsTo(Bus) ? 1f : 0f;
        _send.Snap();
    }

    private void ClassifyUnderruns()
    {
        long readerUnderruns = Reader.Underruns;
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

        if (_feed.LastPacketTicks > _pendingSinceTicks)
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
