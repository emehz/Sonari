using OndaMix.Audio.Core.Buffers;

namespace OndaMix.Audio.Engine.Routing;

/// <summary>
/// Consumidor de un <see cref="SpscRingBuffer{T}"/> alimentado por un dispositivo con otro reloj.
/// <para>
/// Dos tarjetas nunca van exactamente a la misma frecuencia (diferencias típicas de 10–300 ppm). Sin corrección, el
/// buffer se vacía (cortes) o crece sin límite (latencia y luego pérdidas). Este lector remuestrea con interpolación
/// cúbica (Catmull-Rom) y ajusta la relación de lectura de forma continua con un control proporcional sobre el nivel
/// medio del buffer, limitado a ±<see cref="MaxCorrectionPpm"/> ppm (inaudible: 1000 ppm ≈ 1,7 centésimas de semitono).
/// </para>
/// <para>
/// Estados: <b>cebado</b> (devuelve silencio hasta que el buffer alcanza el objetivo) y <b>en marcha</b>. Un vaciado
/// (underrun) vuelve al cebado para no encadenar microcortes. Si el buffer acumula demasiado (p. ej. tras un parón de la
/// salida), se descarta el exceso de golpe (resync) para no arrastrar latencia.
/// </para>
/// Todo el trabajo ocurre en <see cref="Read"/>, en el hilo de render, sin asignar memoria.
/// </summary>
public sealed class DriftCompensatingReader
{
    public const double MaxCorrectionPpm = 1000;

    // Frames anteriores que se conservan entre bloques: la interpolación cúbica necesita un punto antes y dos después.
    private const int HistoryFrames = 4;

    // Corrección por segundo de error de nivel: con 10 ms de exceso se lee 1000 ppm más rápido. Constante de tiempo ≈ 10 s.
    private const double ProportionalGain = 0.1;

    // Suavizado exponencial del nivel por bloque (≈ 0,5 s con bloques de 10 ms): elimina el diente de sierra de los paquetes.
    private const double FillSmoothing = 0.02;

    private readonly SpscRingBuffer<float> _ring;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly int _maxOutputFrames;
    private readonly int _targetFrames;
    private readonly int _resyncThresholdFrames;
    private readonly float[] _work;

    private double _phase;
    private double _smoothedFill;
    private double _ratio = 1.0;
    private bool _priming = true;
    private long _underruns;
    private long _resyncs;

    /// <param name="maxOutputFrames">Mayor bloque que se pedirá en una llamada; los mayores se procesan por partes.</param>
    /// <param name="targetFrames">
    /// Frames que deben quedar en el buffer después de servir cada bloque: margen frente a la irregularidad de llegada
    /// de los paquetes. La latencia que añade este tramo es aproximadamente este valor más medio bloque.
    /// </param>
    public DriftCompensatingReader(SpscRingBuffer<float> ring, int channels, int sampleRate, int maxOutputFrames, int targetFrames)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetFrames, 1);

        _ring = ring;
        _channels = channels;
        _sampleRate = sampleRate;
        _maxOutputFrames = maxOutputFrames;
        _targetFrames = targetFrames;
        _resyncThresholdFrames = targetFrames * 4;

        if ((_resyncThresholdFrames + maxOutputFrames) * channels > ring.Capacity)
        {
            throw new ArgumentException("El ring buffer es demasiado pequeño para el nivel objetivo.", nameof(ring));
        }

        int maxInputFrames = (int)Math.Ceiling(maxOutputFrames * (1 + MaxCorrectionPpm * 1e-6)) + 2;
        _work = new float[(HistoryFrames + maxInputFrames) * channels];
    }

    public int TargetFrames => _targetFrames;

    /// <summary>Nivel medio del buffer en frames (para diagnóstico).</summary>
    public double SmoothedFillFrames => Volatile.Read(ref _smoothedFill);

    /// <summary>Corrección actual de la velocidad de lectura en ppm (positivo = se lee más rápido de lo nominal).</summary>
    public double CorrectionPpm => (Volatile.Read(ref _ratio) - 1.0) * 1e6;

    public long Underruns => Interlocked.Read(ref _underruns);

    public long Resyncs => Interlocked.Read(ref _resyncs);

    public bool IsPriming => Volatile.Read(ref _priming);

    /// <summary>Vuelve al estado de cebado. Solo con el productor y el consumidor detenidos.</summary>
    public void ResetToPriming()
    {
        Volatile.Write(ref _priming, true);
        Volatile.Write(ref _ratio, 1.0);
        Volatile.Write(ref _smoothedFill, 0);
    }

    public void Read(Span<float> output)
    {
        int offset = 0;
        int chunkSamples = _maxOutputFrames * _channels;
        while (offset < output.Length)
        {
            int length = Math.Min(chunkSamples, output.Length - offset);
            ReadChunk(output.Slice(offset, length));
            offset += length;
        }
    }

    private void ReadChunk(Span<float> output)
    {
        int outFrames = output.Length / _channels;

        // Nivel que quedará tras servir este bloque: es el margen real frente a la irregularidad de los paquetes.
        int level = (_ring.AvailableToRead / _channels) - outFrames;

        if (_priming)
        {
            if (level < _targetFrames)
            {
                output.Clear();
                return;
            }

            // Arranque limpio: sin historia antigua y con el nivel medido como punto de partida.
            Array.Clear(_work);
            _phase = 0;
            Volatile.Write(ref _smoothedFill, level);
            Volatile.Write(ref _priming, false);
        }

        if (level > _resyncThresholdFrames)
        {
            int excess = level - _targetFrames;
            _ring.Skip(excess * _channels);
            level -= excess;
            Volatile.Write(ref _smoothedFill, level);
            Interlocked.Increment(ref _resyncs);
        }

        double smoothed = _smoothedFill + FillSmoothing * (level - _smoothedFill);
        Volatile.Write(ref _smoothedFill, smoothed);

        double errorSeconds = (smoothed - _targetFrames) / _sampleRate;
        double maxCorrection = MaxCorrectionPpm * 1e-6;
        double ratio = 1.0 + Math.Clamp(errorSeconds * ProportionalGain, -maxCorrection, maxCorrection);
        Volatile.Write(ref _ratio, ratio);

        double endPosition = _phase + (outFrames * ratio);
        int needed = (int)endPosition;

        Span<float> incoming = _work.AsSpan(HistoryFrames * _channels, needed * _channels);
        int got = _ring.Read(incoming) / _channels;
        if (got < needed)
        {
            incoming[(got * _channels)..].Clear();
            Interlocked.Increment(ref _underruns);
            Volatile.Write(ref _priming, true);
        }

        Interpolate(output, outFrames, ratio);

        // La cola de este bloque es la historia del siguiente.
        _work.AsSpan(needed * _channels, HistoryFrames * _channels).CopyTo(_work);
        _phase = endPosition - needed;
    }

    private void Interpolate(Span<float> output, int outFrames, double ratio)
    {
        float[] w = _work;
        int channels = _channels;
        int outIndex = 0;

        for (int frame = 0; frame < outFrames; frame++)
        {
            double position = _phase + (frame * ratio);
            int i = (int)position;
            float t = (float)(position - i);
            int baseIndex = i * channels;

            for (int channel = 0; channel < channels; channel++)
            {
                int k = baseIndex + channel;
                float ym1 = w[k];
                float y0 = w[k + channels];
                float y1 = w[k + (2 * channels)];
                float y2 = w[k + (3 * channels)];

                float c1 = 0.5f * (y1 - ym1);
                float c2 = ym1 - (2.5f * y0) + (2f * y1) - (0.5f * y2);
                float c3 = (0.5f * (y2 - ym1)) + (1.5f * (y0 - y1));
                output[outIndex++] = (((((c3 * t) + c2) * t) + c1) * t) + y0;
            }
        }
    }
}
