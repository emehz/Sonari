namespace Sonari.Audio.Engine.Dsp;

/// <summary>
/// Ganancia que pasa del valor actual al objetivo con una rampa lineal a lo largo de un bloque, para que mover un
/// fader o silenciar no produzca clics. El objetivo se fija desde cualquier hilo; <see cref="Apply"/> solo desde el de audio.
/// </summary>
public sealed class SmoothedGain(float initial = 1f)
{
    private float _target = initial;
    private float _current = initial;

    public float Target
    {
        get => Volatile.Read(ref _target);
        set => Volatile.Write(ref _target, value);
    }

    /// <summary>Ganancia aplicada al final del último bloque.</summary>
    public float Current => _current;

    /// <summary>Salta al objetivo sin rampa (solo al preparar el motor, nunca con audio sonando).</summary>
    public void Snap() => _current = Target;

    public void Apply(Span<float> interleaved, int channels)
    {
        float target = Target;
        float start = _current;

        if (start == target)
        {
            if (target == 1f)
            {
                return;
            }

            if (target == 0f)
            {
                interleaved.Clear();
                return;
            }

            for (int i = 0; i < interleaved.Length; i++)
            {
                interleaved[i] *= target;
            }

            return;
        }

        int frames = interleaved.Length / channels;
        if (frames == 0)
        {
            return;
        }

        // Se calcula desde el inicio en cada frame (no acumulando) para que el redondeo no desvíe el valor final.
        float step = (target - start) / frames;
        int index = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            float gain = frame == frames - 1 ? target : start + (step * (frame + 1));
            for (int channel = 0; channel < channels; channel++)
            {
                interleaved[index++] *= gain;
            }
        }

        _current = target;
    }
}
