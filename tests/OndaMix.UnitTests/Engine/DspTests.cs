using OndaMix.Audio.Engine.Dsp;

namespace OndaMix.UnitTests.Engine;

public class SmoothedGainTests
{
    [Fact]
    public void Ramp_IsMonotonicAndEndsAtTarget()
    {
        var gain = new SmoothedGain(0f) { Target = 1f };
        var block = new float[480 * 2];
        Array.Fill(block, 1f);

        gain.Apply(block, 2);

        for (int i = 2; i < block.Length; i += 2)
        {
            Assert.True(block[i] >= block[i - 2]);
            Assert.Equal(block[i], block[i + 1]);   // los dos canales reciben la misma ganancia
        }

        Assert.Equal(1f, block[^1], 1e-6f);
        Assert.True(block[0] < 0.01f);
        Assert.Equal(1f, gain.Current);
    }

    [Fact]
    public void AtZero_ClearsTheBlock()
    {
        var gain = new SmoothedGain(0f);
        float[] block = [0.3f, -0.7f, 1f, 0.1f];

        gain.Apply(block, 2);

        Assert.All(block, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void AtUnity_LeavesTheBlockUntouched()
    {
        var gain = new SmoothedGain(1f);
        float[] block = [0.3f, -0.7f];

        gain.Apply(block, 2);

        Assert.Equal([0.3f, -0.7f], block);
    }
}

public class PeakMeterTests
{
    [Fact]
    public void KeepsMaximumAbsoluteValuePerChannelUntilRead()
    {
        var meter = new PeakMeter();

        meter.Process([0.1f, -0.2f, -0.6f, 0.05f], 2);
        meter.Process([0.3f, 0.1f], 2);
        var first = meter.ReadAndReset();
        var second = meter.ReadAndReset();

        Assert.Equal(0.6f, first.Left);
        Assert.Equal(0.2f, first.Right);
        Assert.Equal(0f, second.Max);
    }

    [Fact]
    public void Mono_IsReportedOnBothSides()
    {
        var meter = new PeakMeter();

        meter.Process([0.25f, -0.5f], 1);
        var peak = meter.ReadAndReset();

        Assert.Equal(0.5f, peak.Left);
        Assert.Equal(0.5f, peak.Right);
    }
}

public class SafetyClipperTests
{
    [Fact]
    public void ClampsOverflowAndSanitizesInvalidValues()
    {
        float[] samples = [0.5f, 1.5f, -2f, float.NaN, float.PositiveInfinity, -0.99f];

        int clipped = SafetyClipper.Process(samples);

        Assert.Equal(4, clipped);
        Assert.Equal([0.5f, 1f, -1f, 0f, 1f, -0.99f], samples);
    }
}
