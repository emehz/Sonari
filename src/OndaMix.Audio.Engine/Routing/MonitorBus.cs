using OndaMix.Audio.Engine.Dsp;

namespace OndaMix.Audio.Engine.Routing;

/// <summary>
/// Bus de monitoreo (lo que oyes por los auriculares). Su <see cref="Render"/> lo llama el hilo del dispositivo de
/// salida, que es quien marca el ritmo. Nunca alimenta al bus del micrófono virtual: es una regla de diseño contra la
/// realimentación, no una opción.
/// </summary>
internal sealed class MonitorBus
{
    private readonly MixerChannel[] _channels;
    private readonly BusControl _control;
    private readonly int _channelCount;
    private readonly SmoothedGain _gain = new(0f);
    private readonly float[] _scratch;
    private long _clippedSamples;

    public MonitorBus(BusControl control, int channelCount, params MixerChannel[] channels)
    {
        _control = control;
        _channelCount = channelCount;
        _channels = channels;
        _scratch = new float[MixerChannel.MaxBlockFrames * channelCount];
    }

    public PeakMeter OutputMeter { get; } = new();

    public long ClippedSamples => Interlocked.Read(ref _clippedSamples);

    /// <summary>Hilo de render. No asigna memoria, no bloquea y no lanza excepciones por datos de audio.</summary>
    public void Render(Span<float> output)
    {
        int chunk = _scratch.Length;
        for (int offset = 0; offset < output.Length; offset += chunk)
        {
            RenderChunk(output.Slice(offset, Math.Min(chunk, output.Length - offset)));
        }
    }

    public void ResetForRestart()
    {
        _gain.Target = _control.Linear;
        _gain.Snap();
        foreach (MixerChannel channel in _channels)
        {
            channel.ResetForRestart();
        }
    }

    private void RenderChunk(Span<float> mix)
    {
        mix.Clear();
        foreach (MixerChannel channel in _channels)
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
