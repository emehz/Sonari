namespace Sonari.Audio.Engine.Dsp;

/// <summary>
/// Ganancia independiente por lado (fader × balance) con rampa lineal a lo largo del bloque, como
/// <see cref="SmoothedGain"/> pero para estéreo. Los objetivos se pasan en cada llamada; solo hilo de audio.
/// </summary>
public sealed class StereoGain
{
    private float _left;
    private float _right;

    public float CurrentLeft => _left;

    public float CurrentRight => _right;

    /// <summary>Salta al valor sin rampa (solo al preparar el motor, nunca con audio sonando).</summary>
    public void Snap(float left, float right)
    {
        _left = left;
        _right = right;
    }

    public void Apply(Span<float> interleaved, int channels, float targetLeft, float targetRight)
    {
        if (channels != 2)
        {
            // El motor trabaja en estéreo; con otro número de canales se usa el lado izquierdo para todos.
            ApplyMono(interleaved, channels, targetLeft);
            _right = targetLeft;
            return;
        }

        float startLeft = _left;
        float startRight = _right;
        int frames = interleaved.Length / 2;
        if (frames == 0)
        {
            return;
        }

        if (startLeft == targetLeft && startRight == targetRight)
        {
            if (targetLeft == 1f && targetRight == 1f)
            {
                return;
            }

            for (int i = 0; i < interleaved.Length; i += 2)
            {
                interleaved[i] *= targetLeft;
                interleaved[i + 1] *= targetRight;
            }

            return;
        }

        float stepLeft = (targetLeft - startLeft) / frames;
        float stepRight = (targetRight - startRight) / frames;
        for (int frame = 0; frame < frames; frame++)
        {
            bool last = frame == frames - 1;
            interleaved[2 * frame] *= last ? targetLeft : startLeft + (stepLeft * (frame + 1));
            interleaved[(2 * frame) + 1] *= last ? targetRight : startRight + (stepRight * (frame + 1));
        }

        _left = targetLeft;
        _right = targetRight;
    }

    private void ApplyMono(Span<float> interleaved, int channels, float target)
    {
        int frames = interleaved.Length / channels;
        float start = _left;
        float step = frames > 0 ? (target - start) / frames : 0;
        int index = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            float gain = frame == frames - 1 ? target : start + (step * (frame + 1));
            for (int channel = 0; channel < channels; channel++)
            {
                interleaved[index++] *= gain;
            }
        }

        _left = target;
    }
}
