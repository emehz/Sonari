namespace Sonari.Audio.Effects;

/// <summary>
/// Activa y desactiva un procesador sin clics: en vez de conmutar, cruza la señal original y la procesada en 10 ms.
/// <list type="bullet">
/// <item>Apagado del todo: no procesa nada (no gasta CPU).</item>
/// <item>Encendido del todo: procesa en el sitio, sin copias.</item>
/// <item>Durante el cruce: copia la señal original, procesa y mezcla muestra a muestra.</item>
/// </list>
/// Al encenderse desde apagado se borra el estado del procesador, para que no reaparezcan restos de la última vez
/// (la cola de un eco, por ejemplo).
/// </summary>
internal sealed class EffectSlot
{
    private const double FadeMilliseconds = 10;

    private readonly IAudioProcessor _processor;
    private readonly int _channels;
    private readonly float[] _dry;
    private readonly float _step;
    private float _mix;
    private float _target;

    public EffectSlot(IAudioProcessor processor, int sampleRate, int channels, int maxBlockFrames)
    {
        _processor = processor;
        _channels = channels;
        _dry = new float[maxBlockFrames * channels];
        _step = (float)(1000 / (FadeMilliseconds * sampleRate));
    }

    public bool IsEnabled => _target > 0;

    /// <summary>Totalmente apagado (no queda cruce en curso).</summary>
    public bool IsIdle => _mix == 0 && _target == 0;

    public void SetEnabled(bool enabled)
    {
        if (enabled && IsIdle)
        {
            _processor.Reset();
        }

        _target = enabled ? 1 : 0;
    }

    public void ResetProcessor() => _processor.Reset();

    /// <summary>Salta el cruce: para el estado inicial, cuando todavía no ha sonado nada.</summary>
    public void SetEnabledImmediately(bool enabled)
    {
        SetEnabled(enabled);
        _mix = _target;
    }

    public void Process(Span<float> interleaved)
    {
        if (IsIdle)
        {
            return;
        }

        if (_mix == 1 && _target == 1)
        {
            _processor.Process(interleaved);
            return;
        }

        Span<float> dry = _dry.AsSpan(0, interleaved.Length);
        interleaved.CopyTo(dry);
        _processor.Process(interleaved);

        int channels = _channels;
        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            _mix = _target > _mix ? MathF.Min(_target, _mix + _step) : MathF.Max(_target, _mix - _step);
            for (int c = 0; c < channels; c++)
            {
                int i = offset + c;
                interleaved[i] = dry[i] + ((interleaved[i] - dry[i]) * _mix);
            }
        }
    }
}
