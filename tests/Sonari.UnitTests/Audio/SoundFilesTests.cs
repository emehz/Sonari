using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;
using Sonari.Audio.Engine.Playback;
using Sonari.Audio.Playback;

namespace Sonari.UnitTests.Audio;

/// <summary>Decodificación real con Media Foundation sobre archivos WAV generados en la prueba.</summary>
public sealed class SoundFilesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Sonari.Tests", Guid.NewGuid().ToString("N"));

    public SoundFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string WriteSineWav(int sampleRate, int channels, double seconds, double frequency, string name = "tono.wav")
    {
        string path = Path.Combine(_folder, name);
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
        int frames = (int)(sampleRate * seconds);
        var frame = new float[channels];
        for (int i = 0; i < frames; i++)
        {
            float s = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
            Array.Fill(frame, s);
            writer.WriteSamples(frame, 0, channels);
        }

        return path;
    }

    [Fact]
    public void MonoWavAt44k_IsConvertedTo48kStereo()
    {
        string path = WriteSineWav(44_100, 1, 1.0, 440);
        var decoder = new MediaFoundationSoundDecoder();

        SoundClip clip = decoder.Decode(path, AudioFormat.Default, TestContext.Current.CancellationToken);

        Assert.Equal(AudioFormat.Default, clip.Format);
        Assert.InRange(clip.Frames, 47_500, 48_500);

        int crossings = 0;
        for (int f = 1; f < clip.Frames; f++)
        {
            Assert.Equal(clip.Samples[2 * f], clip.Samples[(2 * f) + 1]);   // mono duplicado en los dos lados
            if (clip.Samples[2 * (f - 1)] < 0 && clip.Samples[2 * f] >= 0)
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, 435, 445);   // sigue siendo un tono de 440 Hz
        Assert.InRange(clip.Samples.Max(), 0.45f, 0.55f);
    }

    [Fact]
    public void MissingFile_ThrowsAReadableError()
    {
        var decoder = new MediaFoundationSoundDecoder();

        var ex = Assert.Throws<SoundDecodeException>(() =>
            decoder.Decode(Path.Combine(_folder, "no-existe.mp3"), AudioFormat.Default, TestContext.Current.CancellationToken));

        Assert.Contains("no-existe.mp3", ex.Message);
    }

    [Fact]
    public void NotAnAudioFile_ThrowsSoundDecodeException()
    {
        string path = Path.Combine(_folder, "falso.mp3");
        File.WriteAllText(path, "esto no es audio");
        var decoder = new MediaFoundationSoundDecoder();

        Assert.Throws<SoundDecodeException>(() => decoder.Decode(path, AudioFormat.Default, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_DecodesEachFileOnlyOnce()
    {
        string path = WriteSineWav(48_000, 2, 0.2, 1000);
        var counting = new CountingDecoder(new MediaFoundationSoundDecoder());
        var cache = new SoundClipCache(counting, NullLogger<SoundClipCache>.Instance);

        SoundClip first = await cache.GetAsync(path, AudioFormat.Default, TestContext.Current.CancellationToken);
        SoundClip second = await cache.GetAsync(path, AudioFormat.Default, TestContext.Current.CancellationToken);
        cache.Forget(path);
        SoundClip third = await cache.GetAsync(path, AudioFormat.Default, TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(2, counting.Calls);
    }

    private sealed class CountingDecoder(ISoundDecoder inner) : ISoundDecoder
    {
        private int _calls;

        public int Calls => _calls;

        public IReadOnlyCollection<string> SupportedExtensions => inner.SupportedExtensions;

        public SoundClip Decode(string path, AudioFormat target, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.Decode(path, target, cancellationToken);
        }
    }
}
