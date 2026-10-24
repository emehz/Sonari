using Sonari.Audio.Core.Effects;

namespace Sonari.Audio.Effects;

/// <summary>El mismo limitador con anticipación de las mezclas, con el techo configurable. Añade 1,5 ms de latencia.</summary>
internal sealed class Limiter : VoiceEffect
{
    private readonly PeakLimiter _limiter;

    public Limiter(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        _limiter = new PeakLimiter(sampleRate, channels);
        Configure(new LimiterSettings());
    }

    public void Configure(LimiterSettings settings) => _limiter.SetCeilingDb(settings.CeilingDb);

    public override void Reset() => _limiter.Reset();

    public override void Process(Span<float> interleaved) => _limiter.Process(interleaved);
}
