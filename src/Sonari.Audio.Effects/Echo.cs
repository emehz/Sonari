using System.Numerics;
using Sonari.Audio.Core.Effects;
using Sonari.Audio.Effects.Dsp;

namespace Sonari.Audio.Effects;

/// <summary>
/// Eco con realimentación. Cada repetición pasa por un pasabajos de un polo a 5 kHz, como en los ecos de cinta: las
/// repeticiones se oscurecen y no se acumulan agudos.
/// <para>
/// Si se cambia el tiempo mientras suena, el retardo se desliza hacia el nuevo valor en unos 50 ms en vez de saltar:
/// un salto produciría un chasquido.
/// </para>
/// </summary>
internal sealed class Echo : VoiceEffect
{
    private const double MaxDelayMilliseconds = 1000;
    private const double DampingHz = 5000;

    private readonly float[][] _buffers;
    private readonly float[] _damping;
    private readonly int _mask;
    private readonly float _dampingCoefficient;
    private readonly float _glide;
    private int _write;
    private float _delay;
    private OnsetRamp _onset;
    private float _targetDelay;
    private float _feedback;
    private float _mix;

    public Echo(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        int length = (int)BitOperations.RoundUpToPowerOf2((uint)(sampleRate * MaxDelayMilliseconds / 1000) + 4);
        _mask = length - 1;
        _onset = new OnsetRamp(sampleRate);
        _buffers = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            _buffers[c] = new float[length];
        }

        _damping = new float[channels];
        _dampingCoefficient = (float)(1 - Math.Exp(-2 * Math.PI * DampingHz / sampleRate));
        _glide = OnePole(50 / 4.6);
        Configure(new EchoSettings());
        _delay = _targetDelay;
    }

    public void Configure(EchoSettings settings)
    {
        _targetDelay = (float)(SampleRate * settings.DelayMilliseconds / 1000);
        _feedback = settings.Feedback;
        _mix = settings.Mix;
    }

    public override void Reset()
    {
        foreach (float[] buffer in _buffers)
        {
            Array.Clear(buffer);
        }

        Array.Clear(_damping);
        _write = 0;
        _delay = _targetDelay;
        _onset.Reset();
    }

    public override void Process(Span<float> interleaved)
    {
        int channels = Channels;
        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            _delay += (_targetDelay - _delay) * _glide;
            float onset = _onset.Next();
            for (int c = 0; c < channels; c++)
            {
                float[] buffer = _buffers[c];
                float input = interleaved[offset + c];
                float delayed = Read(buffer, _delay);

                float damped = _damping[c] + ((delayed - _damping[c]) * _dampingCoefficient);
                _damping[c] = MathF.Abs(damped) < 1e-20f ? 0 : damped;

                buffer[_write] = (input * onset) + (_damping[c] * _feedback);
                interleaved[offset + c] = input + (delayed * _mix);
            }

            _write = (_write + 1) & _mask;
        }
    }

    /// <summary>Muestra de hace <paramref name="delay"/> muestras, con interpolación lineal (la actual aún no está escrita).</summary>
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
