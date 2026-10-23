using Sonari.Audio.Engine.Dsp;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Bus de monitoreo (lo que oyes por los auriculares). Su <see cref="Render"/> lo llama el hilo del dispositivo de
/// salida, que es quien marca el ritmo. Nunca alimenta al bus del micrófono virtual: es una regla de diseño contra la
/// realimentación, no una opción.
/// </summary>
internal sealed class MonitorBus
{
    private readonly BusControl _control;
    private readonly int _channelCount;
    private readonly SmoothedGain _gain = new(0f);
    private readonly float[] _scratch;
    private MixerChannel[] _channels = [];
    private long _clippedSamples;

    public MonitorBus(BusControl control, int channelCount)
    {
        _control = control;
        _channelCount = channelCount;
        _scratch = new float[MixerChannel.MaxBlockFrames * channelCount];
    }

    public PeakMeter OutputMeter { get; } = new();

    public long ClippedSamples => Interlocked.Read(ref _clippedSamples);

    public long Resyncs
    {
        get
        {
            long total = 0;
            foreach (MixerChannel channel in Volatile.Read(ref _channels))
            {
                total += channel.MonitorReader.Resyncs;
            }

            return total;
        }
    }

    /// <summary>
    /// Sustituye la lista de canales. El hilo de render toma la lista una vez por bloque, así que añadir o quitar una
    /// fuente no detiene ni bloquea el audio de las demás.
    /// </summary>
    public void SetChannels(MixerChannel[] channels) => Volatile.Write(ref _channels, channels);

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
        _gain.Target = _control.Linear;
        _gain.Snap();
        foreach (MixerChannel channel in Volatile.Read(ref _channels))
        {
            channel.ResetForRestart();
        }
    }

    private void RenderChunk(Span<float> mix)
    {
        mix.Clear();
        foreach (MixerChannel channel in Volatile.Read(ref _channels))
        {
            channel.MixInto(mix, _scratch);
        }

        _gain.Target = _control.Linear;
        _gain.Apply(mix, _channelCount);

        int clipped = SafetyClipper.Process(mix);
        if (clipped > 0)
        {
            Interlocked.Add(ref _clippedSamples, clipped);
        }

        OutputMeter.Process(mix, _channelCount);
    }
}
