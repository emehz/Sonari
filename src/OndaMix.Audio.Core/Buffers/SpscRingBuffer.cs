using System.Numerics;

namespace OndaMix.Audio.Core.Buffers;

/// <summary>
/// Buffer circular sin bloqueos para exactamente un productor y un consumidor.
/// <para>
/// Pensado para la ruta de audio: <see cref="Write"/> y <see cref="Read"/> no asignan memoria ni bloquean.
/// El productor (hilo de captura) solo llama a <see cref="Write"/>; el consumidor (hilo de render) solo llama a
/// <see cref="Read"/>, <see cref="Skip"/> y <see cref="DiscardAll"/>. Las propiedades de consulta pueden leerse
/// desde cualquier hilo, aunque su valor puede quedar desfasado al instante.
/// </para>
/// </summary>
public sealed class SpscRingBuffer<T> where T : unmanaged
{
    public const int MaxCapacity = 1 << 30;

    private readonly T[] _buffer;
    private readonly int _mask;

    // Contadores monótonos de 64 bits: no se desbordan durante la vida del proceso.
    // _writePosition solo lo modifica el productor; _readPosition, solo el consumidor.
    private long _writePosition;
    private long _readPosition;

    public SpscRingBuffer(int minimumCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumCapacity, MaxCapacity);

        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minimumCapacity);
        _buffer = new T[capacity];
        _mask = capacity - 1;
    }

    /// <summary>Capacidad real: la potencia de dos mayor o igual que la solicitada.</summary>
    public int Capacity => _buffer.Length;

    public int AvailableToRead => (int)(Volatile.Read(ref _writePosition) - Volatile.Read(ref _readPosition));

    public int AvailableToWrite => Capacity - AvailableToRead;

    /// <summary>
    /// Copia tantos elementos de <paramref name="source"/> como quepan y devuelve cuántos ha escrito. Si son menos que
    /// <c>source.Length</c>, el resto se pierde (overrun) y el llamador debe contabilizarlo.
    /// </summary>
    public int Write(ReadOnlySpan<T> source)
    {
        long write = _writePosition;
        long read = Volatile.Read(ref _readPosition);
        int free = Capacity - (int)(write - read);
        int count = Math.Min(free, source.Length);
        if (count <= 0)
        {
            return 0;
        }

        int start = (int)(write & _mask);
        int firstPart = Math.Min(count, Capacity - start);
        source[..firstPart].CopyTo(_buffer.AsSpan(start, firstPart));
        source.Slice(firstPart, count - firstPart).CopyTo(_buffer.AsSpan(0, count - firstPart));

        // Publica los datos antes de avanzar la posición (semántica release).
        Volatile.Write(ref _writePosition, write + count);
        return count;
    }

    /// <summary>
    /// Copia hasta <c>destination.Length</c> elementos y devuelve cuántos ha leído. Si son menos, el llamador decide cómo
    /// rellenar el hueco (normalmente con silencio) y lo contabiliza como underrun.
    /// </summary>
    public int Read(Span<T> destination)
    {
        long read = _readPosition;
        long write = Volatile.Read(ref _writePosition);
        int count = Math.Min((int)(write - read), destination.Length);
        if (count <= 0)
        {
            return 0;
        }

        int start = (int)(read & _mask);
        int firstPart = Math.Min(count, Capacity - start);
        _buffer.AsSpan(start, firstPart).CopyTo(destination);
        _buffer.AsSpan(0, count - firstPart).CopyTo(destination[firstPart..]);

        Volatile.Write(ref _readPosition, read + count);
        return count;
    }

    /// <summary>Descarta hasta <paramref name="count"/> elementos sin copiarlos (recorte de latencia). Solo para el consumidor.</summary>
    public int Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        long read = _readPosition;
        long write = Volatile.Read(ref _writePosition);
        int skipped = Math.Min((int)(write - read), count);
        Volatile.Write(ref _readPosition, read + skipped);
        return skipped;
    }

    /// <summary>Descarta todo lo pendiente. Solo para el consumidor.</summary>
    public void DiscardAll() => Volatile.Write(ref _readPosition, Volatile.Read(ref _writePosition));
}
