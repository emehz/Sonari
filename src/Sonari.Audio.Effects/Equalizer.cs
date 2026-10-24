using Sonari.Audio.Core.Effects;
using Sonari.Audio.Effects.Dsp;

namespace Sonari.Audio.Effects;

/// <summary>Tres bandas en serie: estante de graves, campana de medios (Q 0,9) y estante de agudos.</summary>
internal sealed class Equalizer : VoiceEffect
{
    private const double MidQ = 0.9;

    private readonly Biquad _low;
    private readonly Biquad _mid;
    private readonly Biquad _high;

    public Equalizer(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        _low = new Biquad(channels);
        _mid = new Biquad(channels);
        _high = new Biquad(channels);
        Configure(new EqualizerSettings());
    }

    public void Configure(EqualizerSettings settings)
    {
        _low.Coefficients = BiquadCoefficients.LowShelf(SampleRate, EqualizerSettings.LowHz, settings.LowDb);
        _mid.Coefficients = BiquadCoefficients.Peaking(SampleRate, EqualizerSettings.MidHz, MidQ, settings.MidDb);
        _high.Coefficients = BiquadCoefficients.HighShelf(SampleRate, EqualizerSettings.HighHz, settings.HighDb);
    }

    public override void Reset()
    {
        _low.Reset();
        _mid.Reset();
        _high.Reset();
    }

    public override void Process(Span<float> interleaved)
    {
        _low.Process(interleaved);
        _mid.Process(interleaved);
        _high.Process(interleaved);
    }
}
