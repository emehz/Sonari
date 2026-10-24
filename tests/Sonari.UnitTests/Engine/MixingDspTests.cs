using Sonari.Audio.Effects;
using Sonari.Audio.Engine.Dsp;
using Sonari.Audio.Engine.Routing;

namespace Sonari.UnitTests.Engine;

public class PeakLimiterTests
{
    private const int SampleRate = 48_000;

    private static float[] Sine(double amplitude, double frequency, int frames, int channels = 2)
    {
        var data = new float[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            float s = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * f / SampleRate));
            for (int c = 0; c < channels; c++)
            {
                data[(f * channels) + c] = s;
            }
        }

        return data;
    }

    [Theory]
    [InlineData(2.0, 100)]
    [InlineData(4.0, 1000)]
    [InlineData(1.2, 5000)]
    public void LoudSignal_NeverExceedsTheCeiling(double amplitude, double frequency)
    {
        var limiter = new PeakLimiter(SampleRate, 2);
        float[] block = Sine(amplitude, frequency, SampleRate);

        for (int offset = 0; offset < block.Length; offset += 960)
        {
            limiter.Process(block.AsSpan(offset, Math.Min(960, block.Length - offset)));
        }

        float max = block.Max(MathF.Abs);
        Assert.True(max <= limiter.Ceiling * 1.0001f, $"Pico {max} por encima del techo {limiter.Ceiling}");
        Assert.True(limiter.ReadAndResetGainReductionDb() > 1f);
    }

    [Fact]
    public void QuietSignal_PassesUnchangedApartFromTheLookaheadDelay()
    {
        var limiter = new PeakLimiter(SampleRate, 2);
        float[] input = Sine(0.5, 440, 4800);
        float[] output = (float[])input.Clone();

        limiter.Process(output);

        int delay = limiter.LatencyFrames * 2;
        for (int i = delay; i < output.Length; i++)
        {
            Assert.Equal(input[i - delay], output[i], 1e-6f);
        }

        Assert.Equal(0f, limiter.ReadAndResetGainReductionDb());
    }

    [Fact]
    public void SingleSampleSpike_IsCaughtByTheLookahead()
    {
        var limiter = new PeakLimiter(SampleRate, 2);
        var block = new float[2000];
        block[1000] = 3f;
        block[1001] = -3f;

        limiter.Process(block);

        Assert.True(block.Max(MathF.Abs) <= limiter.Ceiling * 1.0001f);
    }

    [Fact]
    public void Gain_RecoversAfterTheLoudPartEnds()
    {
        var limiter = new PeakLimiter(SampleRate, 2);
        limiter.Process(Sine(3.0, 200, SampleRate / 2));

        float[] quiet = Sine(0.5, 200, SampleRate);
        limiter.Process(quiet);

        // Tras ~1 s con release de 100 ms la ganancia vuelve a 1: el final sale como entra.
        float lastSecondPeak = quiet.AsSpan(quiet.Length - 4800).ToArray().Max(MathF.Abs);
        Assert.Equal(0.5f, lastSecondPeak, 0.005f);
    }
}

public class StereoBalanceTests
{
    [Theory]
    [InlineData(0f, 1f, 1f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(-1f, 1f, 0f)]
    [InlineData(-0.5f, 1f, 0.5f)]
    [InlineData(0.25f, 0.75f, 1f)]
    public void BalanceLaw_AttenuatesOnlyTheOppositeSide(float balance, float expectedLeft, float expectedRight)
    {
        var control = new ChannelControl { Balance = balance };

        (float left, float right) = control.StereoGains;

        Assert.Equal(expectedLeft, left, 1e-6f);
        Assert.Equal(expectedRight, right, 1e-6f);
    }

    [Fact]
    public void Balance_IsClampedAndCombinedWithTheFader()
    {
        var control = new ChannelControl { Balance = 5f, GainDb = -6.0206f };

        (float left, float right) = control.StereoGains;

        Assert.Equal(1f, control.Balance);
        Assert.Equal(0f, left, 1e-6f);
        Assert.Equal(0.5f, right, 1e-4f);
    }

    [Fact]
    public void StereoGain_RampsEachSideWithoutJumps()
    {
        var gain = new StereoGain();
        gain.Snap(1f, 1f);
        var block = new float[480 * 2];
        Array.Fill(block, 1f);

        gain.Apply(block, 2, 0f, 1f);

        for (int i = 2; i < block.Length; i += 2)
        {
            Assert.True(block[i] <= block[i - 2]);   // izquierda baja poco a poco
            Assert.Equal(1f, block[i + 1]);           // derecha intacta
        }

        Assert.Equal(0f, block[^2], 1e-6f);
    }
}
