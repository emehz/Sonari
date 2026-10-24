using Sonari.Audio.Core.Effects;
using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Effects;

/// <summary>
/// Puerta de ruido con histéresis y retención:
/// <list type="bullet">
/// <item>Se abre en cuanto la envolvente supera el umbral (ataque de 1 ms).</item>
/// <item>Se mantiene abierta mientras no baje 6 dB por debajo, y después 50 ms más (retención): así no corta el final de
/// las palabras ni tartamudea con niveles cerca del umbral.</item>
/// <item>Cerrada atenúa 80 dB, en un tiempo de <see cref="NoiseGateSettings.ReleaseMilliseconds"/>.</item>
/// </list>
/// La detección usa el máximo de los canales, así que la puerta es la misma para todos.
/// </summary>
internal sealed class NoiseGate : VoiceEffect
{
    private const float HysteresisDb = 6;
    private const float ClosedGain = 1e-4f;   // −80 dB.

    private readonly float _attack;
    private readonly float _envelopeRelease;
    private readonly int _holdFrames;
    private float _openThreshold;
    private float _closeThreshold;
    private float _release;
    private float _envelope;
    private float _gain;
    private int _holdRemaining;
    private bool _open;

    public NoiseGate(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        _attack = OnePole(1);
        _envelopeRelease = OnePole(10);
        _holdFrames = sampleRate * 50 / 1000;
        Configure(new NoiseGateSettings());
        Reset();
    }

    public void Configure(NoiseGateSettings settings)
    {
        _openThreshold = Decibels.ToLinear(settings.ThresholdDb);
        _closeThreshold = Decibels.ToLinear(settings.ThresholdDb - HysteresisDb);
        _release = OnePole(settings.ReleaseMilliseconds / 4.6);   // ~99 % del cambio en el tiempo indicado.
    }

    public override void Reset()
    {
        _envelope = 0;
        _gain = ClosedGain;
        _holdRemaining = 0;
        _open = false;
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

            _envelope = peak > _envelope ? peak : _envelope + ((peak - _envelope) * _envelopeRelease);

            if (_envelope >= _openThreshold || (_open && _envelope >= _closeThreshold))
            {
                _open = true;
                _holdRemaining = _holdFrames;
            }
            else if (_holdRemaining > 0)
            {
                _holdRemaining--;
            }
            else
            {
                _open = false;
            }

            float target = _open ? 1f : ClosedGain;
            _gain += (target - _gain) * (target > _gain ? _attack : _release);

            for (int c = 0; c < channels; c++)
            {
                interleaved[offset + c] *= _gain;
            }
        }
    }
}
