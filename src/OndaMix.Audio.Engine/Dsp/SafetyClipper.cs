namespace OndaMix.Audio.Engine.Dsp;

/// <summary>
/// Última protección antes del dispositivo: recorta a [-1, 1] y sustituye NaN/infinito por silencio.
/// No es un limitador (eso llega en la Fase 4): solo evita que un error numérico llegue a los oídos o al driver.
/// </summary>
public static class SafetyClipper
{
    /// <returns>Número de muestras que hubo que recortar o sanear.</returns>
    public static int Process(Span<float> samples)
    {
        int clipped = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i];
            if (sample > 1f)
            {
                samples[i] = 1f;
                clipped++;
            }
            else if (sample < -1f)
            {
                samples[i] = -1f;
                clipped++;
            }
            else if (float.IsNaN(sample))
            {
                samples[i] = 0f;
                clipped++;
            }
        }

        return clipped;
    }
}
