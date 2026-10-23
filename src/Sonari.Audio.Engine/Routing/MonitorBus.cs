using Sonari.Audio.Core.Formats;
using Sonari.Audio.Engine.Dsp;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Bus de monitoreo (lo que oyes por los auriculares). Su <see cref="Render"/> lo llama el hilo del dispositivo de
/// salida, que es quien marca el ritmo. Nunca alimenta al bus del micrófono virtual: es una regla de diseño contra la
/// realimentación, no una opción.
/// <para>Cadena: suma de canales → volumen del bus (y silencio global) → limitador (−0,3 dBFS) → protección final.</para>
/// </summary>
internal sealed class MonitorBus
{
    private readonly BusControl _control;
    private readonly Func<bool> _isGlobalMuted;
    private readonly int _channelCount;
    private readonly SmoothedGain _gain = new(0f);
    private readonly PeakLimiter _limiter;
    private readonly float[] _scratch;
    private ChannelStrip[] _channels = [];
    private long _clippedSamples;

    public MonitorBus(BusControl control, AudioFormat format, Func<bool> isGlobalMuted)
    {
        _control = control;
        _isGlobalMuted = isGlobalMuted;
        _channelCount = format.Channels;
        _limiter = new PeakLimiter(format.SampleRate, format.Channels);
        _scratch = new float[MixerChannel.MaxBlockFrames * format.Channels];
    }

    public PeakMeter OutputMeter { get; } = new();

    public PeakLimiter Limiter => _limiter;

    public long ClippedSamples => Interlocked.Read(ref _clippedSamples);

    public long Resyncs
    {
        get
        {
            long total = 0;
            foreach (ChannelStrip channel in Volatile.Read(ref _channels))
            {
                if (channel is MixerChannel captured)
                {
                    total += captured.MonitorReader.Resyncs;
                }
            }

            return total;
        }
    }

    /// <summary>
    /// Sustituye la lista de canales. El hilo de render toma la lista una vez por bloque, así que añadir o quitar una
    /// fuente no detiene ni bloquea el audio de las demás.
    /// </summary>
    public void SetChannels(ChannelStrip[] channels) => Volatile.Write(ref _channels, channels);

    /// <summary>Hilo de render. No asigna memoria, no bloquea y no lanza excepciones por datos de audio.</summary>
    public void Render(Span<float> output)
    {
        int chunk = _scratch.Length;
        for (int offset = 0; offset < output.Length; offset += chunk)
        {
            RenderChunk(output.Slice(offset, Math.Min(chunk, output.Length - offset)));
        }
    }

    /// <summary>Solo con la salida parada.</summary>
    public void ResetForRestart()
    {
        _gain.Target = TargetGain();
        _gain.Snap();
        _limiter.Reset();
        foreach (ChannelStrip channel in Volatile.Read(ref _channels))
        {
            channel.ResetForRestart();
        }
    }

    private float TargetGain() => _isGlobalMuted() ? 0f : _control.Linear;

    private void RenderChunk(Span<float> mix)
    {
        mix.Clear();
        foreach (ChannelStrip channel in Volatile.Read(ref _channels))
        {
            channel.MixInto(mix, _scratch);
        }

        _gain.Target = TargetGain();
        _gain.Apply(mix, _channelCount);
        _limiter.Process(mix);

        int clipped = SafetyClipper.Process(mix);
        if (clipped > 0)
        {
            Interlocked.Add(ref _clippedSamples, clipped);
        }

        OutputMeter.Process(mix, _channelCount);
    }
}
