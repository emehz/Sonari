using System.Numerics;
using Sonari.Audio.Core.Effects;
using Sonari.Audio.Effects.Dsp;

namespace Sonari.Audio.Effects;

/// <summary>
/// Cambio de tono en el dominio del tiempo con una línea de retardo y dos lecturas cruzadas («delay-line pitch shifter»):
/// <list type="bullet">
/// <item>Si el retardo de lectura varía a ritmo constante, el sonido sale con otra frecuencia (efecto Doppler): con
/// relación de tono r, el retardo cambia (1 − r) muestras por muestra.</item>
/// <item>Cuando el retardo llega al final de la ventana, salta al otro extremo. Para que el salto no se oiga hay dos
/// lecturas separadas media ventana, con ganancias sen²/cos²: la que salta siempre está a volumen cero.</item>
/// </list>
/// Latencia media de media ventana (20 ms) y coste mínimo. A cambio deja una ligera aspereza (las dos lecturas no
/// siempre están en fase) y no conserva los formantes. Para una voz de efecto es lo esperable; para una voz «natural»
/// harían falta PSOLA o un vocoder de fase.
/// </summary>
internal sealed class PitchShifter : VoiceEffect
{
    private const double WindowMilliseconds = 40;

    private readonly float[][] _buffers;
    private readonly int _mask;
    private readonly float _window;
    private int _write;
    private float _delay;
    private OnsetRamp _onset;
    private float _ratio = 1;
    private bool _active;

    public PitchShifter(int sampleRate, int channels)
        : base(sampleRate, channels)
    {
        _window = (float)Math.Round(sampleRate * WindowMilliseconds / 1000);
        int length = (int)BitOperations.RoundUpToPowerOf2((uint)(_window * 2) + 4);
        _mask = length - 1;
        _onset = new OnsetRamp(sampleRate);
        _buffers = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            _buffers[c] = new float[length];
        }

        Reset();
    }

    public void Configure(PitchShiftSettings settings)
    {
        _ratio = MathF.Pow(2, settings.Semitones / 12);

        // Con el tono sin cambiar, las dos lecturas sumarían la señal consigo misma retrasada (filtro en peine):
        // mejor dejarla pasar tal cual.
        _active = MathF.Abs(settings.Semitones) >= 0.01f;
    }

    public override void Reset()
    {
        foreach (float[] buffer in _buffers)
        {
            Array.Clear(buffer);
        }

        _write = 0;
        _delay = 0;
        _onset.Reset();
    }

    public override void Process(Span<float> interleaved)
    {
        if (!_active)
        {
            return;
        }

        int channels = Channels;
        float window = _window;
        float halfWindow = window / 2;
        float step = 1 - _ratio;

        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            float delayA = _delay;
            float delayB = delayA + halfWindow;
            if (delayB >= window)
            {
                delayB -= window;
            }

            // sen²(π·d/W) es 0 en los extremos de la ventana, justo donde la lectura salta.
            float s = MathF.Sin(MathF.PI * delayA / window);
            float gainA = s * s;
            float gainB = 1 - gainA;
            float onset = _onset.Next();

            for (int c = 0; c < channels; c++)
            {
                float[] buffer = _buffers[c];
                buffer[_write] = interleaved[offset + c] * onset;
                interleaved[offset + c] = (gainA * Read(buffer, delayA)) + (gainB * Read(buffer, delayB));
            }

            _write = (_write + 1) & _mask;
            _delay += step;
            if (_delay >= window)
            {
                _delay -= window;
            }
            else if (_delay < 0)
            {
                _delay += window;
            }
        }
    }

    /// <summary>Muestra de hace <paramref name="delay"/> muestras (0 = la recién escrita), con interpolación lineal.</summary>
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
