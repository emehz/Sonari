using System.Numerics;
using Sonari.Audio.Core.Effects;
using Sonari.Audio.Effects.Dsp;

namespace Sonari.Audio.Effects;

/// <summary>
/// Tres efectos de modulación:
/// <list type="bullet">
/// <item><b>Robot</b>: modulación en anillo, la voz multiplicada por un seno de 20–200 Hz. Cada frecuencia f de la voz
/// se convierte en f ± portadora: suena metálica y sin afinación.</item>
/// <item><b>Coro</b>: la voz retrasada 20 ms ± hasta 8 ms con un barrido lento, mezclada con la original. El barrido de
/// cada canal va desfasado 90°, lo que abre el estéreo.</item>
/// <item><b>Flanger</b>: lo mismo con 1–6 ms y realimentación: el filtro en peine resultante barre el espectro.</item>
/// </list>
/// </summary>
internal sealed class Modulation : VoiceEffect
{
    private const float ChorusBaseMs = 20;
    private const float ChorusDepthMs = 8;
    private const float FlangerBaseMs = 1;
    private const float FlangerDepthMs = 5;
    private const float FlangerFeedback = 0.6f;

    private readonly float[][] _buffers;
    private readonly int _mask;
    private int _write;
    private double _phase;
    private OnsetRamp _onset;
    private ModulationMode _mode;
    private double _phaseStep;
    private float _baseDelay;
    private float _depthDelay;
    private float _feedback;
    private float _mix;

    public Modulation(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        int length = (int)BitOperations.RoundUpToPowerOf2((uint)(sampleRate * (ChorusBaseMs + ChorusDepthMs + 2) / 1000));
        _mask = length - 1;
        _onset = new OnsetRamp(sampleRate);
        _buffers = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            _buffers[c] = new float[length];
        }

        Configure(new ModulationSettings());
        Reset();
    }

    public void Configure(ModulationSettings settings)
    {
        _mode = settings.Mode;
        _mix = settings.Mix;
        float perMs = SampleRate / 1000f;
        switch (_mode)
        {
            case ModulationMode.Robot:
                _phaseStep = 2 * Math.PI * settings.RobotFrequencyHz / SampleRate;
                break;
            case ModulationMode.Chorus:
                _phaseStep = 2 * Math.PI * settings.RateHz / SampleRate;
                _baseDelay = ChorusBaseMs * perMs;
                _depthDelay = ChorusDepthMs * perMs * settings.Depth;
                _feedback = 0;
                break;
            default:
                _phaseStep = 2 * Math.PI * settings.RateHz / SampleRate;
                _baseDelay = (FlangerBaseMs * perMs) + 1;
                _depthDelay = FlangerDepthMs * perMs * settings.Depth;
                _feedback = FlangerFeedback;
                break;
        }
    }

    public override void Reset()
    {
        foreach (float[] buffer in _buffers)
        {
            Array.Clear(buffer);
        }

        _write = 0;
        _phase = 0;
        _onset.Reset();
    }

    public override void Process(Span<float> interleaved)
    {
        if (_mode == ModulationMode.Robot)
        {
            ProcessRing(interleaved);
        }
        else
        {
            ProcessDelay(interleaved);
        }
    }

    private void ProcessRing(Span<float> interleaved)
    {
        int channels = Channels;
        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            float carrier = (float)Math.Sin(_phase);
            AdvancePhase();
            for (int c = 0; c < channels; c++)
            {
                float dry = interleaved[offset + c];
                interleaved[offset + c] = dry + (((dry * carrier) - dry) * _mix);
            }
        }
    }

    private void ProcessDelay(Span<float> interleaved)
    {
        int channels = Channels;
        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            float onset = _onset.Next();
            for (int c = 0; c < channels; c++)
            {
                double lfo = Math.Sin(_phase + (c * Math.PI / 2));
                float delay = _baseDelay + (_depthDelay * 0.5f * (1 + (float)lfo));
                float[] buffer = _buffers[c];
                float dry = interleaved[offset + c];
                float wet = Read(buffer, delay);
                float stored = (dry * onset) + (wet * _feedback);
                buffer[_write] = MathF.Abs(stored) < 1e-20f ? 0 : stored;
                interleaved[offset + c] = dry + ((wet - dry) * _mix);
            }

            _write = (_write + 1) & _mask;
            AdvancePhase();
        }
    }

    private void AdvancePhase()
    {
        _phase += _phaseStep;
        if (_phase >= 2 * Math.PI)
        {
            _phase -= 2 * Math.PI;
        }
    }

    /// <summary>Muestra de hace <paramref name="delay"/> muestras (≥ 1: la actual aún no está escrita).</summary>
    private float Read(float[] buffer, float delay)
    {
        float position = _write - delay;
        int index = (int)MathF.Floor(position);
        float fraction = position - index;
        float a = buffer[index & _mask];
        float b = buffer[(index + 1) & _mask];
        return a + ((b - a) * fraction);
    }
}
