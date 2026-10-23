using Sonari.Audio.Core.Buffers;
using Sonari.Audio.Engine.Routing;

namespace Sonari.UnitTests.Engine;

public class DriftCompensatingReaderTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BlockFrames = 480;      // 10 ms
    private const int TargetFrames = 720;     // 15 ms, el valor por defecto del motor

    private static (SpscRingBuffer<float> Ring, DriftCompensatingReader Reader) Create()
    {
        var ring = new SpscRingBuffer<float>(SampleRate * Channels);
        var reader = new DriftCompensatingReader(ring, Channels, SampleRate, MixerChannel.MaxBlockFrames, TargetFrames);
        return (ring, reader);
    }

    private static void WriteConstant(SpscRingBuffer<float> ring, int frames, float value)
    {
        var data = new float[frames * Channels];
        Array.Fill(data, value);
        ring.Write(data);
    }

    [Fact]
    public void WhilePriming_OutputsSilence()
    {
        var (ring, reader) = Create();
        WriteConstant(ring, TargetFrames, 0.5f);   // le falta un bloque para cubrir el objetivo después de leer
        var output = new float[BlockFrames * Channels];
        Array.Fill(output, 9f);

        reader.Read(output);

        Assert.All(output, s => Assert.Equal(0f, s));
        Assert.True(reader.IsPriming);
        Assert.Equal(0, reader.Underruns);
    }

    [Fact]
    public void ConstantSignal_PassesThroughUnchanged()
    {
        var (ring, reader) = Create();
        var output = new float[BlockFrames * Channels];

        WriteConstant(ring, TargetFrames + (3 * BlockFrames), 0.5f);
        for (int i = 0; i < 3; i++)
        {
            reader.Read(output);
            WriteConstant(ring, BlockFrames, 0.5f);
        }

        // Tras unos frames de historia, una entrada constante debe salir constante (la interpolación no inventa nada).
        Assert.All(output, s => Assert.Equal(0.5f, s, 1e-5f));
        Assert.False(reader.IsPriming);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(-300)]
    [InlineData(80)]
    public void ClockDrift_IsAbsorbedWithoutUnderrunsOrResyncs(double driftPpm)
    {
        var (ring, reader) = Create();
        var output = new float[BlockFrames * Channels];
        var packet = new float[(BlockFrames + 2) * Channels];
        double produced = 0;

        // 10 minutos simulados: el productor (micro) va driftPpm más rápido o más lento que el consumidor (auriculares).
        const int blocks = 60_000;
        WriteConstant(ring, TargetFrames + BlockFrames, 0.1f);
        for (int block = 0; block < blocks; block++)
        {
            produced += BlockFrames * (1 + (driftPpm * 1e-6));
            int frames = (int)produced;
            produced -= frames;
            ring.Write(packet.AsSpan(0, frames * Channels));
            reader.Read(output);
        }

        Assert.Equal(0, reader.Underruns);
        Assert.Equal(0, reader.Resyncs);

        // El regulador compensa la deriva real y mantiene el nivel cerca del objetivo (error de régimen ≈ deriva / ganancia).
        Assert.Equal(driftPpm, reader.CorrectionPpm, tolerance: 15.0);
        double levelErrorMs = (reader.SmoothedFillFrames - TargetFrames) * 1000.0 / SampleRate;
        Assert.InRange(levelErrorMs, -5, 5);
    }

    [Fact]
    public void Underrun_IsCountedAndReturnsToPriming()
    {
        var (ring, reader) = Create();
        var output = new float[BlockFrames * Channels];
        WriteConstant(ring, TargetFrames + BlockFrames, 0.2f);
        reader.Read(output);          // arranca y deja ≈ TargetFrames

        // El productor deja de enviar: los siguientes bloques vacían el buffer.
        for (int i = 0; i < 3; i++)
        {
            reader.Read(output);
        }

        Assert.Equal(1, reader.Underruns);
        Assert.True(reader.IsPriming);
    }

    [Fact]
    public void LargeBacklog_IsDroppedWithAResync()
    {
        var (ring, reader) = Create();
        var output = new float[BlockFrames * Channels];
        WriteConstant(ring, TargetFrames + BlockFrames, 0f);
        reader.Read(output);

        // Simula un parón de la salida de 200 ms mientras la captura seguía llegando.
        WriteConstant(ring, SampleRate / 5, 0f);
        reader.Read(output);

        Assert.Equal(1, reader.Resyncs);
        int remaining = ring.AvailableToRead / Channels;
        Assert.InRange(remaining, TargetFrames - BlockFrames, TargetFrames + BlockFrames);
    }

    [Fact]
    public void Sine_StaysContinuousAcrossBlocksWhileCorrecting()
    {
        var (ring, reader) = Create();
        const double frequency = 1000;
        long phaseIndex = 0;
        var packet = new float[BlockFrames * Channels];
        var output = new float[BlockFrames * Channels];
        float previous = 0;
        float maxStep = 0;
        double sumSquares = 0;
        long counted = 0;

        void Produce()
        {
            for (int f = 0; f < BlockFrames; f++)
            {
                float s = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * phaseIndex++ / SampleRate));
                packet[2 * f] = s;
                packet[(2 * f) + 1] = s;
            }

            ring.Write(packet);
        }

        // Exceso inicial de 10 ms: obliga al regulador a leer más rápido de lo nominal durante toda la prueba.
        for (int i = 0; i < 4; i++)
        {
            Produce();
        }

        for (int block = 0; block < 500; block++)
        {
            Produce();
            reader.Read(output);
            if (reader.IsPriming)
            {
                continue;
            }

            for (int f = 0; f < BlockFrames; f++)
            {
                float s = output[2 * f];
                if (block > 2)
                {
                    maxStep = MathF.Max(maxStep, MathF.Abs(s - previous));
                    sumSquares += s * s;
                    counted++;
                }

                previous = s;
            }
        }

        // Un seno de 1 kHz y amplitud 0,5 cambia como mucho 0,5·2π·1000/48000 ≈ 0,065 por muestra; un salto mayor sería un clic.
        Assert.True(maxStep < 0.07f, $"Discontinuidad detectada: salto de {maxStep}");
        Assert.Equal(0.5 / Math.Sqrt(2), Math.Sqrt(sumSquares / counted), 0.01);
        Assert.True(reader.CorrectionPpm > 0);
        Assert.Equal(0, reader.Underruns);
    }
}
