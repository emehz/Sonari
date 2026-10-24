namespace Sonari.Audio.Effects.Dsp;

/// <summary>
/// Coeficientes de un filtro bicuadrático normalizados (a0 = 1). Fórmulas del «Audio EQ Cookbook» de Robert
/// Bristow-Johnson, las de referencia para ecualizadores.
/// </summary>
internal readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    public static BiquadCoefficients Identity { get; } = new(1, 0, 0, 0, 0);

    public static BiquadCoefficients HighPass(int sampleRate, double frequency, double q = 0.7071)
    {
        var (cos, alpha) = Prepare(sampleRate, frequency, q);
        return Normalize((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static BiquadCoefficients LowPass(int sampleRate, double frequency, double q = 0.7071)
    {
        var (cos, alpha) = Prepare(sampleRate, frequency, q);
        return Normalize((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static BiquadCoefficients Peaking(int sampleRate, double frequency, double q, double gainDb)
    {
        var (cos, alpha) = Prepare(sampleRate, frequency, q);
        double a = Math.Pow(10, gainDb / 40);
        return Normalize(1 + (alpha * a), -2 * cos, 1 - (alpha * a), 1 + (alpha / a), -2 * cos, 1 - (alpha / a));
    }

    /// <summary>Estante de graves con pendiente S = 1.</summary>
    public static BiquadCoefficients LowShelf(int sampleRate, double frequency, double gainDb)
    {
        var (cos, alpha) = Prepare(sampleRate, frequency, q: Math.Sqrt(0.5));
        double a = Math.Pow(10, gainDb / 40);
        double twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;
        return Normalize(
            a * ((a + 1) - ((a - 1) * cos) + twoSqrtAAlpha),
            2 * a * ((a - 1) - ((a + 1) * cos)),
            a * ((a + 1) - ((a - 1) * cos) - twoSqrtAAlpha),
            (a + 1) + ((a - 1) * cos) + twoSqrtAAlpha,
            -2 * ((a - 1) + ((a + 1) * cos)),
            (a + 1) + ((a - 1) * cos) - twoSqrtAAlpha);
    }

    /// <summary>Estante de agudos con pendiente S = 1.</summary>
    public static BiquadCoefficients HighShelf(int sampleRate, double frequency, double gainDb)
    {
        var (cos, alpha) = Prepare(sampleRate, frequency, q: Math.Sqrt(0.5));
        double a = Math.Pow(10, gainDb / 40);
        double twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;
        return Normalize(
            a * ((a + 1) + ((a - 1) * cos) + twoSqrtAAlpha),
            -2 * a * ((a - 1) + ((a + 1) * cos)),
            a * ((a + 1) + ((a - 1) * cos) - twoSqrtAAlpha),
            (a + 1) - ((a - 1) * cos) + twoSqrtAAlpha,
            2 * ((a - 1) - ((a + 1) * cos)),
            (a + 1) - ((a - 1) * cos) - twoSqrtAAlpha);
    }

    private static (double Cos, double Alpha) Prepare(int sampleRate, double frequency, double q)
    {
        // Por debajo de Nyquist con margen: a frecuencias cercanas a fs/2 los coeficientes degeneran.
        double f = Math.Clamp(frequency, 10, sampleRate * 0.45);
        double w0 = 2 * Math.PI * f / sampleRate;
        return (Math.Cos(w0), Math.Sin(w0) / (2 * q));
    }

    private static BiquadCoefficients Normalize(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
}

/// <summary>
/// Filtro bicuadrático para audio entrelazado, un estado por canal. Forma directa II transpuesta en doble precisión:
/// estable con frecuencias de corte bajas (un pasaaltos a 20 Hz con float pierde precisión).
/// </summary>
internal sealed class Biquad(int channels)
{
    private readonly double[] _z1 = new double[channels];
    private readonly double[] _z2 = new double[channels];

    public BiquadCoefficients Coefficients { get; set; } = BiquadCoefficients.Identity;

    public void Reset()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
    }

    public void Process(Span<float> interleaved)
    {
        BiquadCoefficients c = Coefficients;
        int channels = _z1.Length;
        for (int ch = 0; ch < channels; ch++)
        {
            double z1 = _z1[ch];
            double z2 = _z2[ch];
            for (int i = ch; i < interleaved.Length; i += channels)
            {
                double x = interleaved[i];
                double y = (c.B0 * x) + z1;
                z1 = (c.B1 * x) - (c.A1 * y) + z2;
                z2 = (c.B2 * x) - (c.A2 * y);
                interleaved[i] = (float)y;
            }

            // Sin esto, el silencio tras una nota deja el estado en valores subnormales, que son muy lentos de calcular.
            _z1[ch] = Math.Abs(z1) < 1e-20 ? 0 : z1;
            _z2[ch] = Math.Abs(z2) < 1e-20 ? 0 : z2;
        }
    }

    /// <summary>Procesa una sola muestra de un canal (para efectos que van muestra a muestra).</summary>
    public float ProcessSample(int channel, float input)
    {
        BiquadCoefficients c = Coefficients;
        double x = input;
        double y = (c.B0 * x) + _z1[channel];
        _z1[channel] = (c.B1 * x) - (c.A1 * y) + _z2[channel];
        _z2[channel] = (c.B2 * x) - (c.A2 * y);
        return (float)y;
    }
}
