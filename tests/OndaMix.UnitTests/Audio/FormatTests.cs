using OndaMix.Audio.Core.Formats;

namespace OndaMix.UnitTests.Audio;

public class DecibelsTests
{
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(-6.0206f, 0.5f)]
    [InlineData(-20f, 0.1f)]
    [InlineData(6.0206f, 2f)]
    public void ToLinear_MatchesKnownValues(float db, float expectedLinear)
    {
        Assert.Equal(expectedLinear, Decibels.ToLinear(db), 0.0001f);
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(0.5f, -6.0206f)]
    [InlineData(0.001f, -60f)]
    public void FromLinear_MatchesKnownValues(float linear, float expectedDb)
    {
        Assert.Equal(expectedDb, Decibels.FromLinear(linear), 0.001f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(1e-12f)]
    public void FromLinear_ClampsSilenceAndInvalidValuesToFloor(float linear)
    {
        Assert.Equal(Decibels.Floor, Decibels.FromLinear(linear));
    }

    [Fact]
    public void ToLinear_AtOrBelowFloor_IsExactSilence()
    {
        Assert.Equal(0f, Decibels.ToLinear(Decibels.Floor));
        Assert.Equal(0f, Decibels.ToLinear(-200f));
    }
}

public class AudioFormatTests
{
    [Fact]
    public void Default_Is48kStereo()
    {
        Assert.Equal(48_000, AudioFormat.Default.SampleRate);
        Assert.Equal(2, AudioFormat.Default.Channels);
    }

    [Fact]
    public void FramesAndSamples_For10Milliseconds()
    {
        var format = new AudioFormat(48_000, 2);

        Assert.Equal(480, format.FramesFor(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(960, format.SamplesFor(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(TimeSpan.FromMilliseconds(10), format.DurationOf(480));
    }

    [Fact]
    public void FramesFor_RoundsUp()
    {
        // 44,1 kHz · 10 ms = 441 frames exactos; 1 ms = 44,1 → 45.
        var format = new AudioFormat(44_100, 2);

        Assert.Equal(441, format.FramesFor(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(45, format.FramesFor(TimeSpan.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData(7_999, 2)]
    [InlineData(192_001, 2)]
    [InlineData(48_000, 0)]
    [InlineData(48_000, 9)]
    public void Constructor_RejectsOutOfRangeValues(int sampleRate, int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFormat(sampleRate, channels));
    }
}
