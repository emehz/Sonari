using Sonari.Audio.Effects;

namespace Sonari.UnitTests.Effects;

/// <summary>Señales de prueba y medidas para los efectos (estéreo entrelazado a 48 kHz).</summary>
internal static class Signals
{
    public const int Rate = 48_000;
    public const int Channels = 2;

    public static float[] Sine(double frequency, double amplitude, double seconds)
    {
        int frames = (int)(seconds * Rate);
        var data = new float[frames * Channels];
        for (int i = 0; i < frames; i++)
        {
            float value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate));
            data[i * 2] = value;
            data[(i * 2) + 1] = value;
        }

        return data;
    }

    public static float[] Constant(float value, double seconds)
    {
        var data = new float[(int)(seconds * Rate) * Channels];
        Array.Fill(data, value);
        return data;
    }

    public static float[] Impulse(double seconds)
    {
        var data = new float[(int)(seconds * Rate) * Channels];
        data[0] = 1;
        data[1] = 1;
        return data;
    }

    public static float[] Noise(double amplitude, double seconds, int seed = 1)
    {
        var random = new Random(seed);
        var data = new float[(int)(seconds * Rate) * Channels];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(amplitude * ((random.NextDouble() * 2) - 1));
        }

        return data;
    }

    public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Procesa en bloques de 10 ms, como el motor.</summary>
    public static float[] Run(VoiceEffect effect, float[] input)
    {
        float[] data = (float[])input.Clone();
        const int block = 480 * Channels;
        for (int offset = 0; offset < data.Length; offset += block)
        {
            effect.Process(data.AsSpan(offset, Math.Min(block, data.Length - offset)));
        }

        return data;
    }

    /// <summary>Amplitud de la componente de <paramref name="frequency"/> en el canal izquierdo (Goertzel).</summary>
    public static double Amplitude(float[] interleaved, double frequency, double fromSeconds, double seconds)
    {
        int start = (int)(fromSeconds * Rate);
        int count = (int)(seconds * Rate);
        double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / Rate);
        double s1 = 0, s2 = 0;
        for (int i = 0; i < count; i++)
        {
            double s0 = interleaved[(start + i) * Channels] + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        double power = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return 2 * Math.Sqrt(Math.Max(0, power)) / count;
    }

    public static double Db(double ratio) => 20 * Math.Log10(ratio);

    public static float Peak(float[] interleaved, double fromSeconds = 0, double seconds = double.MaxValue)
    {
        int start = (int)(fromSeconds * Rate) * Channels;
        int end = (int)Math.Min(interleaved.Length, start + (Math.Min(seconds, 1e6) * Rate * Channels));
        float peak = 0;
        for (int i = start; i < end; i++)
        {
            peak = MathF.Max(peak, MathF.Abs(interleaved[i]));
        }

        return peak;
    }

    public static double Energy(float[] interleaved, double fromSeconds, double seconds)
    {
        int start = (int)(fromSeconds * Rate) * Channels;
        int end = Math.Min(interleaved.Length, start + (int)(seconds * Rate * Channels));
        double sum = 0;
        for (int i = start; i < end; i++)
        {
            sum += interleaved[i] * (double)interleaved[i];
        }

        return sum;
    }

    /// <summary>Mayor salto entre dos muestras seguidas del canal izquierdo: un clic aparece como un salto grande.</summary>
    public static float MaxStep(float[] interleaved)
    {
        float max = 0;
        for (int i = Channels; i < interleaved.Length; i += Channels)
        {
            max = MathF.Max(max, MathF.Abs(interleaved[i] - interleaved[i - Channels]));
        }

        return max;
    }

    public static bool AllFinite(float[] data) => data.All(float.IsFinite);
}
