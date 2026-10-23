using OndaMix.Audio.Core.Buffers;

namespace OndaMix.UnitTests.Audio;

public class SpscRingBufferTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 8)]
    [InlineData(480, 512)]
    [InlineData(1024, 1024)]
    public void Capacity_IsRoundedUpToPowerOfTwo(int requested, int expected)
    {
        var ring = new SpscRingBuffer<float>(requested);

        Assert.Equal(expected, ring.Capacity);
        Assert.Equal(0, ring.AvailableToRead);
        Assert.Equal(expected, ring.AvailableToWrite);
    }

    [Fact]
    public void WriteThenRead_ReturnsSameSamplesInOrder()
    {
        var ring = new SpscRingBuffer<float>(8);
        float[] input = [0.1f, 0.2f, 0.3f];
        var output = new float[3];

        Assert.Equal(3, ring.Write(input));
        Assert.Equal(3, ring.Read(output));
        Assert.Equal(input, output);
        Assert.Equal(0, ring.AvailableToRead);
    }

    [Fact]
    public void WriteAndRead_WrapAroundTheEndOfTheArray()
    {
        var ring = new SpscRingBuffer<int>(8);
        var scratch = new int[8];

        ring.Write([1, 2, 3, 4, 5, 6]);
        ring.Read(scratch.AsSpan(0, 5));        // posición de lectura = 5

        Assert.Equal(6, ring.Write([7, 8, 9, 10, 11, 12]));   // da la vuelta
        int read = ring.Read(scratch);

        Assert.Equal(7, read);
        Assert.Equal([6, 7, 8, 9, 10, 11, 12], scratch[..read]);
    }

    [Fact]
    public void Write_WhenFull_WritesOnlyWhatFits()
    {
        var ring = new SpscRingBuffer<int>(4);

        int written = ring.Write([1, 2, 3, 4, 5, 6]);

        Assert.Equal(4, written);
        Assert.Equal(0, ring.AvailableToWrite);
        Assert.Equal(0, ring.Write([7]));
    }

    [Fact]
    public void Read_WhenEmpty_ReturnsZeroAndLeavesDestinationUntouched()
    {
        var ring = new SpscRingBuffer<float>(4);
        float[] destination = [9f, 9f];

        Assert.Equal(0, ring.Read(destination));
        Assert.Equal([9f, 9f], destination);
    }

    [Fact]
    public void Read_WhenPartiallyFilled_ReturnsAvailableCount()
    {
        var ring = new SpscRingBuffer<int>(8);
        ring.Write([1, 2]);
        var destination = new int[5];

        Assert.Equal(2, ring.Read(destination));
        Assert.Equal([1, 2, 0, 0, 0], destination);
    }

    [Fact]
    public void Skip_DropsOldestSamples()
    {
        var ring = new SpscRingBuffer<int>(8);
        ring.Write([1, 2, 3, 4]);

        Assert.Equal(3, ring.Skip(3));
        var destination = new int[4];
        Assert.Equal(1, ring.Read(destination));
        Assert.Equal(4, destination[0]);
        Assert.Equal(0, ring.Skip(10));
    }

    [Fact]
    public void DiscardAll_EmptiesTheBuffer()
    {
        var ring = new SpscRingBuffer<int>(8);
        ring.Write([1, 2, 3]);

        ring.DiscardAll();

        Assert.Equal(0, ring.AvailableToRead);
        Assert.Equal(8, ring.AvailableToWrite);
    }

    [Fact]
    public void Constructor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpscRingBuffer<float>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpscRingBuffer<float>(SpscRingBuffer<float>.MaxCapacity + 1));
    }

    /// <summary>
    /// Un productor y un consumidor reales en hilos distintos, con tamaños de bloque que no encajan con la capacidad
    /// (como un paquete WASAPI de 441 frames contra un bloque de mezcla de 480). No debe perderse ni duplicarse nada.
    /// </summary>
    [Fact]
    public async Task ConcurrentProducerAndConsumer_PreserveSequence()
    {
        const int total = 2_000_000;
        var ring = new SpscRingBuffer<int>(1024);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Task producer = Task.Run(() =>
        {
            var chunk = new int[441];
            int next = 0;
            while (next < total)
            {
                int length = Math.Min(chunk.Length, total - next);
                for (int i = 0; i < length; i++)
                {
                    chunk[i] = next + i;
                }

                int offset = 0;
                while (offset < length)
                {
                    ct.ThrowIfCancellationRequested();
                    int written = ring.Write(chunk.AsSpan(offset, length - offset));
                    if (written == 0)
                    {
                        Thread.Yield();
                    }

                    offset += written;
                }

                next += length;
            }
        }, ct);

        Task<int> consumer = Task.Run(() =>
        {
            var chunk = new int[480];
            int expected = 0;
            while (expected < total)
            {
                ct.ThrowIfCancellationRequested();
                int read = ring.Read(chunk);
                if (read == 0)
                {
                    Thread.Yield();
                    continue;
                }

                for (int i = 0; i < read; i++)
                {
                    if (chunk[i] != expected)
                    {
                        throw new InvalidOperationException($"Se esperaba {expected} y llegó {chunk[i]}");
                    }

                    expected++;
                }
            }

            return expected;
        }, ct);

        await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(30), ct);

        Assert.Equal(total, await consumer);
        Assert.Equal(0, ring.AvailableToRead);
    }
}
