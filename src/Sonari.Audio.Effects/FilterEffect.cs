using Sonari.Audio.Core.Effects;
using Sonari.Audio.Effects.Dsp;

namespace Sonari.Audio.Effects;

/// <summary>Pasaaltos y pasabajos de 12 dB/octava (Butterworth). El pasabajos se salta si está en su máximo.</summary>
internal sealed class FilterEffect : VoiceEffect
{
    private readonly Biquad _highPass;
    private readonly Biquad _lowPass;
    private bool _lowPassActive;

    public FilterEffect(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        _highPass = new Biquad(channels);
        _lowPass = new Biquad(channels);
        Configure(new FilterSettings());
    }

    public void Configure(FilterSettings settings)
    {
        _highPass.Coefficients = BiquadCoefficients.HighPass(SampleRate, settings.HighPassHz);
        bool lowPassActive = settings.LowPassHz < FilterSettings.LowPassOffHz;
        if (lowPassActive && !_lowPassActive)
        {
            _lowPass.Reset();
        }

        _lowPassActive = lowPassActive;
        _lowPass.Coefficients = BiquadCoefficients.LowPass(SampleRate, settings.LowPassHz);
    }

    public override void Reset()
    {
        _highPass.Reset();
        _lowPass.Reset();
    }

    public override void Process(Span<float> interleaved)
    {
        _highPass.Process(interleaved);
        if (_lowPassActive)
        {
            _lowPass.Process(interleaved);
        }
    }
}
