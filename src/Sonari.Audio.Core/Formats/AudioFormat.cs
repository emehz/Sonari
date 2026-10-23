namespace Sonari.Audio.Core.Formats;

/// <summary>
/// Formato interno del motor: float32 entrelazado. Solo varían la frecuencia y el número de canales.
/// </summary>
public readonly record struct AudioFormat
{
    public const int MinSampleRate = 8_000;
    public const int MaxSampleRate = 192_000;
    public const int MaxChannels = 8;

    public AudioFormat(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, MinSampleRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, MaxSampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, MaxChannels);

        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>48 kHz estéreo: formato predeterminado del proyecto.</summary>
    public static AudioFormat Default { get; } = new(48_000, 2);

    /// <summary>Frames (una muestra por canal) que caben en <paramref name="duration"/>, redondeado hacia arriba.</summary>
    public int FramesFor(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks);
        return (int)Math.Ceiling(duration.TotalSeconds * SampleRate);
    }

    /// <summary>Muestras float entrelazadas que caben en <paramref name="duration"/>.</summary>
    public int SamplesFor(TimeSpan duration) => FramesFor(duration) * Channels;

    public TimeSpan DurationOf(int frames) => TimeSpan.FromSeconds((double)frames / SampleRate);

    public override string ToString() => $"{SampleRate / 1000.0:0.###} kHz · {Channels} ch · float32";
}
