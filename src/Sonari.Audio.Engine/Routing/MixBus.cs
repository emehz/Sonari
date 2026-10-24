using Sonari.Audio.Core.Formats;
using Sonari.Audio.Engine.Dsp;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Una mezcla (monitoreo o micrófono virtual). Su <see cref="Render"/> lo llama el hilo de su dispositivo de salida.
/// <para>Cadena: suma de entradas → volumen de la mezcla (y silencio global) → limitador (−0,3 dBFS) → protección final.</para>
/// Las dos mezclas comparten fuentes pero nunca se alimentan entre sí: lo que oyes por los auriculares no puede llegar al
/// micrófono virtual. Es una regla de la estructura, no una opción.
/// </summary>
internal sealed class MixBus
{
    private readonly BusControl _control;
    private readonly Func<bool> _isGlobalMuted;
    private readonly int _channelCount;
    private readonly SmoothedGain _gain = new(0f);
    private readonly float[] _scratch;
    private BusInput[] _inputs = [];
    private int _active;
    private long _clippedSamples;

    public MixBus(BusKind kind, BusControl control, AudioFormat format, Func<bool> isGlobalMuted)
    {
        Kind = kind;
        _control = control;
        _isGlobalMuted = isGlobalMuted;
        _channelCount = format.Channels;
        Limiter = new PeakLimiter(format.SampleRate, format.Channels);
        _scratch = new float[SourceFeed.MaxBlockFrames * format.Channels];
    }

    public BusKind Kind { get; }

    public PeakMeter OutputMeter { get; } = new();

    public PeakLimiter Limiter { get; }

    public long ClippedSamples => Interlocked.Read(ref _clippedSamples);

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public long Resyncs => Volatile.Read(ref _inputs).Sum(input => input.Reader.Resyncs);

    /// <summary>
    /// Sustituye la lista de entradas. El hilo de render la toma una vez por bloque, así que añadir o quitar una fuente
    /// no detiene ni bloquea el audio de las demás.
    /// </summary>
    public void SetInputs(BusInput[] inputs)
    {
        bool active = IsActive;
        foreach (BusInput input in inputs)
        {
            input.IsActive = active;
        }

        Volatile.Write(ref _inputs, inputs);
    }

    /// <summary>Marca la mezcla como sonando o parada; los productores dejan de escribir en una mezcla parada.</summary>
    public void SetActive(bool active)
    {
        Volatile.Write(ref _active, active ? 1 : 0);
        foreach (BusInput input in Volatile.Read(ref _inputs))
        {
            input.IsActive = active;
        }
    }

    /// <summary>Hilo de render. No asigna memoria, no bloquea y no lanza excepciones por datos de audio.</summary>
    public void Render(Span<float> output)
    {
        int chunk = _scratch.Length;
        for (int offset = 0; offset < output.Length; offset += chunk)
        {
            RenderChunk(output.Slice(offset, Math.Min(chunk, output.Length - offset)));
        }
    }

    /// <summary>Solo con la salida de esta mezcla parada.</summary>
    public void ResetForRestart()
    {
        _gain.Target = TargetGain();
        _gain.Snap();
        Limiter.Reset();
        foreach (BusInput input in Volatile.Read(ref _inputs))
        {
            input.ResetForRestart();
        }
    }

    private float TargetGain() => _isGlobalMuted() ? 0f : _control.Linear;

    private void RenderChunk(Span<float> mix)
    {
        mix.Clear();
        foreach (BusInput input in Volatile.Read(ref _inputs))
        {
            input.MixInto(mix, _scratch);
        }

        _gain.Target = TargetGain();
        _gain.Apply(mix, _channelCount);
        Limiter.Process(mix);

        int clipped = SafetyClipper.Process(mix);
        if (clipped > 0)
        {
            Interlocked.Add(ref _clippedSamples, clipped);
        }

        OutputMeter.Process(mix, _channelCount);
    }
}
