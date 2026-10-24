using Sonari.Audio.Core.Effects;
using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Effects;

/// <summary>
/// Compresor de pico con rodilla suave de 6 dB, enlazado entre canales.
/// La reducción se calcula en dB (curva estática) y se suaviza con ataque y release independientes.
/// </summary>
internal sealed class Compressor : VoiceEffect
{
    private const float KneeDb = 6;

    private float _threshold;
    private float _slope;
    private float _attack;
    private float _release;
    private float _makeupDb;
    private float _reductionDb;

    public Compressor(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        Configure(new CompressorSettings());
    }

    /// <summary>Reducción actual en dB (positiva). Solo para pruebas y diagnóstico.</summary>
    public float GainReductionDb => -_reductionDb;

    public void Configure(CompressorSettings settings)
    {
        _threshold = settings.ThresholdDb;
        _slope = (1 / settings.Ratio) - 1;
        _attack = OnePole(settings.AttackMilliseconds);
        _release = OnePole(settings.ReleaseMilliseconds);
        _makeupDb = settings.MakeupDb;
    }

    public override void Reset() => _reductionDb = 0;

    /// <summary>Reducción estática (dB, ≤ 0) para un nivel de entrada en dB.</summary>
    public float StaticReductionDb(float levelDb)
    {
        float over = levelDb - _threshold;
        if (2 * over <= -KneeDb)
        {
            return 0;
        }

        if (2 * MathF.Abs(over) < KneeDb)
        {
            float x = over + (KneeDb / 2);
            return _slope * x * x / (2 * KneeDb);
        }

        return _slope * over;
    }

    public override void Process(Span<float> interleaved)
    {
        int channels = Channels;
        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            float peak = 0;
            for (int c = 0; c < channels; c++)
            {
                peak = MathF.Max(peak, MathF.Abs(interleaved[offset + c]));
            }

            float target = peak > 1e-6f ? StaticReductionDb(Decibels.FromLinear(peak)) : 0;

            // Más reducción = ataque; menos = release.
            _reductionDb += (target - _reductionDb) * (target < _reductionDb ? _attack : _release);
            float gain = Decibels.ToLinear(_reductionDb + _makeupDb);

            for (int c = 0; c < channels; c++)
            {
                interleaved[offset + c] *= gain;
            }
        }
    }
}
