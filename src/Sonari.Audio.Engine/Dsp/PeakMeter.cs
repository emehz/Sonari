using OndaMix.Audio.Core.Engine;

namespace OndaMix.Audio.Engine.Dsp;

/// <summary>
/// Medidor de pico estéreo sin bloqueos. El hilo de audio acumula el máximo de cada bloque y la UI lo lee y
/// lo reinicia; así no se pierde ningún pico aunque la UI lea a 30 Hz y el audio vaya a 100 bloques por segundo.
/// </summary>
public sealed class PeakMeter
{
    // Los floats se guardan como bits en int para poder usar Interlocked.
    private int _leftBits;
    private int _rightBits;

    public void Process(ReadOnlySpan<float> interleaved, int channels)
    {
        float left = 0f;
        float right = 0f;

        if (channels == 1)
        {
            foreach (float sample in interleaved)
            {
                left = MathF.Max(left, MathF.Abs(sample));
            }

            right = left;
        }
        else
        {
            for (int i = 0; i + 1 < interleaved.Length; i += channels)
            {
                left = MathF.Max(left, MathF.Abs(interleaved[i]));
                right = MathF.Max(right, MathF.Abs(interleaved[i + 1]));
            }
        }

        StoreMax(ref _leftBits, left);
        StoreMax(ref _rightBits, right);
    }

    public StereoPeak ReadAndReset() => new(
        BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _leftBits, 0)),
        BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _rightBits, 0)));

    private static void StoreMax(ref int bits, float value)
    {
        // Los floats positivos conservan el orden al compararlos como enteros, así que basta con comparar bits.
        int candidate = BitConverter.SingleToInt32Bits(value);
        int current = Volatile.Read(ref bits);
        while (candidate > current)
        {
            int observed = Interlocked.CompareExchange(ref bits, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
