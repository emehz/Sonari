using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Engine.Dsp;

/// <summary>
/// Limitador de picos con anticipación (lookahead), enlazado entre canales. Garantiza que ninguna muestra supera el
/// techo sin recortarla: reduce la ganancia un poco antes de que llegue el pico.
/// <para>
/// Algoritmo (mínimo móvil + media móvil, como el limitador de Signalsmith Audio):
/// <list type="number">
/// <item>Para cada frame se calcula la ganancia necesaria <c>r = min(1, techo / pico)</c>.</item>
/// <item><c>h</c> = mínimo de <c>r</c> en los últimos L frames; luego se deja subir despacio (release).</item>
/// <item><c>g</c> = media de <c>h</c> en los últimos L frames, aplicada a la muestra retrasada L−1 frames.</item>
/// </list>
/// Cada valor que entra en la media de un frame cubre ese frame en su ventana del mínimo, así que la media nunca supera
/// la ganancia que ese frame necesita: no hay picos por encima del techo y la ganancia cambia sin saltos.
/// </para>
/// Sin asignaciones en <see cref="Process"/>. Coste O(L) por frame (L ≈ 64), despreciable para un bus.
/// </summary>
public sealed class PeakLimiter
{
    private readonly int _channels;
    private readonly int _length;
    private readonly float _ceiling;
    private readonly float _releaseCoefficient;
    private readonly float[] _delay;
    private readonly float[] _required;
    private readonly float[] _held;
    private int _position;
    private double _heldSum;
    private float _releasedHold = 1f;
    private int _minGainBits = BitConverter.SingleToInt32Bits(1f);

    public PeakLimiter(int sampleRate, int channels, double lookaheadMilliseconds = 1.5, double releaseMilliseconds = 100,
        float ceilingDb = -0.3f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _channels = channels;
        _length = Math.Max(2, (int)Math.Round(sampleRate * lookaheadMilliseconds / 1000));
        _ceiling = Decibels.ToLinear(ceilingDb);
        _releaseCoefficient = (float)(1 - Math.Exp(-1.0 / (sampleRate * releaseMilliseconds / 1000)));
        _delay = new float[_length * channels];
        _required = new float[_length];
        _held = new float[_length];
        Reset();
    }

    /// <summary>Latencia que añade el limitador, en frames.</summary>
    public int LatencyFrames => _length - 1;

    public float Ceiling => _ceiling;

    public void Reset()
    {
        Array.Clear(_delay);
        Array.Fill(_required, 1f);
        Array.Fill(_held, 1f);
        _heldSum = _length;
        _releasedHold = 1f;
        _position = 0;
    }

    /// <summary>Máxima reducción de ganancia (en dB, positiva) desde la lectura anterior. Seguro desde cualquier hilo.</summary>
    public float ReadAndResetGainReductionDb()
    {
        float minGain = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _minGainBits, BitConverter.SingleToInt32Bits(1f)));
        return minGain >= 1f ? 0f : -Decibels.FromLinear(minGain);
    }

    public void Process(Span<float> interleaved)
    {
        int channels = _channels;
        float blockMinGain = 1f;

        for (int offset = 0; offset + channels <= interleaved.Length; offset += channels)
        {
            float peak = 0f;
            for (int c = 0; c < channels; c++)
            {
                peak = MathF.Max(peak, MathF.Abs(interleaved[offset + c]));
            }

            int pos = _position;
            _required[pos] = peak > _ceiling ? _ceiling / peak : 1f;

            float hold = 1f;
            for (int i = 0; i < _length; i++)
            {
                hold = MathF.Min(hold, _required[i]);
            }

            // Bajar es inmediato (lo exige la garantía); subir es gradual para no modular la señal (release).
            _releasedHold = hold < _releasedHold ? hold : _releasedHold + ((hold - _releasedHold) * _releaseCoefficient);

            _heldSum += _releasedHold - _held[pos];
            _held[pos] = _releasedHold;
            float gain = (float)(_heldSum / _length);
            blockMinGain = MathF.Min(blockMinGain, gain);

            // El frame que sale es el más antiguo del retardo (L−1 frames atrás), que ocupa la posición siguiente.
            int delayIn = pos * channels;
            int outPos = pos + 1 == _length ? 0 : pos + 1;
            int delayOut = outPos * channels;
            for (int c = 0; c < channels; c++)
            {
                float incoming = interleaved[offset + c];
                _delay[delayIn + c] = incoming;
                interleaved[offset + c] = _delay[delayOut + c] * gain;
            }

            _position = outPos;
            if (outPos == 0)
            {
                // Recalcula la suma de vez en cuando para que el error de redondeo no se acumule.
                double sum = 0;
                foreach (float value in _held)
                {
                    sum += value;
                }

                _heldSum = sum;
            }
        }

        StoreMin(blockMinGain);
    }

    private void StoreMin(float gain)
    {
        int candidate = BitConverter.SingleToInt32Bits(gain);
        int current = Volatile.Read(ref _minGainBits);
        while (candidate < current)
        {
            int observed = Interlocked.CompareExchange(ref _minGainBits, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
