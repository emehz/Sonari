using Sonari.Audio.Core.Effects;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Effects.Dsp;

namespace Sonari.Audio.Effects;

/// <summary>
/// Saturación suave con tanh, seguida de un pasabajos de tono.
/// <para>
/// Compensación de nivel: la salida se escala para que una señal de −12 dBFS (un nivel típico de voz) salga con el
/// mismo pico que entra. Sin ella, subir la saturación subiría también el volumen. El pico de salida nunca supera 1.
/// </para>
/// </summary>
internal sealed class Distortion : VoiceEffect
{
    private const float ReferenceLevel = 0.25f;   // −12 dBFS.

    private readonly Biquad _tone;
    private float _drive = 1;
    private float _outputGain = 1;
    private float _mix = 1;

    public Distortion(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        _tone = new Biquad(channels);
        Configure(new DistortionSettings());
    }

    public void Configure(DistortionSettings settings)
    {
        _drive = Decibels.ToLinear(settings.DriveDb);
        _outputGain = ReferenceLevel / MathF.Tanh(_drive * ReferenceLevel);
        _mix = settings.Mix;
        _tone.Coefficients = BiquadCoefficients.LowPass(SampleRate, settings.ToneHz);
    }

    public override void Reset() => _tone.Reset();

    public override void Process(Span<float> interleaved)
    {
        int channels = Channels;
        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            for (int c = 0; c < channels; c++)
            {
                float dry = interleaved[offset + c];
                float wet = _tone.ProcessSample(c, MathF.Tanh(dry * _drive) * _outputGain);
                interleaved[offset + c] = dry + ((wet - dry) * _mix);
            }
        }
    }
}
