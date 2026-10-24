namespace Sonari.Audio.Effects.Dsp;

/// <summary>
/// Rampa de 0 a 1 en 10 ms tras reiniciar un efecto con línea de retardo. Se aplica a lo que se escribe en la línea, no a
/// la salida: así la grabación empieza desde cero en vez de a mitad de onda. Sin ella, cuando la lectura retrasada
/// alcanza el principio de la grabación (20 ms después en el cambio de tono, 300 ms en un eco…), se oye un clic.
/// </summary>
internal struct OnsetRamp(int sampleRate)
{
    private readonly float _step = 1f / Math.Max(1, sampleRate / 100);
    private float _gain;

    public void Reset() => _gain = 0;

    /// <summary>Ganancia para el frame actual; avanza la rampa.</summary>
    public float Next()
    {
        float gain = _gain;
        if (gain < 1)
        {
            _gain = MathF.Min(1, gain + _step);
        }

        return gain;
    }
}
