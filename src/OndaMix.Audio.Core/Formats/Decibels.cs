namespace OndaMix.Audio.Core.Formats;

/// <summary>Conversión entre ganancia lineal y decibelios (dBFS para niveles de señal).</summary>
public static class Decibels
{
    /// <summary>Suelo que representa el silencio. Por debajo, la ganancia lineal es 0.</summary>
    public const float Floor = -96f;

    public static float ToLinear(float db) => db <= Floor ? 0f : MathF.Pow(10f, db / 20f);

    public static float FromLinear(float linear)
    {
        // La comparación negada también cubre NaN.
        if (!(linear > 0f))
        {
            return Floor;
        }

        return MathF.Max(Floor, 20f * MathF.Log10(linear));
    }
}
