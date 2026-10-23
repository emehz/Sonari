using Microsoft.Extensions.Logging.Abstractions;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;
using Sonari.Audio.Engine;

namespace Sonari.UnitTests.Engine;

/// <summary>Sonidos, silencio global y limitador dentro del motor completo, con dispositivos simulados.</summary>
public sealed class EngineMixTests : IAsyncDisposable
{
    private static readonly EngineOptions Options = new(AudioFormat.Default, 10, InputDeviceId: null, MonitorDeviceId: null);

    private readonly FakeDeviceFactory _devices = new();
    private readonly AudioEngine _engine;

    public EngineMixTests()
    {
        _engine = new AudioEngine(
            _devices, _devices, new FakeProcessCaptureFactory(), new FakeProcessLocator(), watcher: null,
            NullLogger<AudioEngine>.Instance);
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    private float RenderPeak(int blocks, float micLevel = 0f)
    {
        var output = new float[480 * 2];
        float max = 0;
        for (int i = 0; i < blocks; i++)
        {
            _devices.Captures[^1].Capture(micLevel, 480);
            _devices.Renders[^1].Render(output);
            max = MathF.Max(max, output.Max(MathF.Abs));
        }

        return max;
    }

    private static SoundClip Constant(float value, int frames)
    {
        var samples = new float[frames * 2];
        Array.Fill(samples, value);
        return new SoundClip(samples, AudioFormat.Default, "prueba");
    }

    [Fact]
    public void SoundChannel_IsFixedAndHeardByDefault()
    {
        Assert.Equal(SourceKind.SoundPlayer, _engine.SoundChannel.Kind);
        Assert.True(_engine.SoundChannel.SendToMonitor);
        Assert.Throws<InvalidOperationException>(() => _engine.RemoveSource(_engine.SoundChannel));
    }

    [Fact]
    public async Task Sounds_ReachTheMonitor()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        _engine.Sounds.Play(Guid.NewGuid(), Constant(0.4f, 48_000), 0, loop: false);

        Assert.Equal(0.4f, RenderPeak(10), 1e-4f);
        Assert.Equal(SourceStatus.Active, _engine.SoundChannel.Status);
    }

    [Fact]
    public async Task GlobalMute_SilencesTheMonitorAndIsReversible()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        _engine.Sounds.Play(Guid.NewGuid(), Constant(0.4f, 48_000), 0, loop: true);
        RenderPeak(5);

        _engine.IsGlobalMuted = true;
        RenderPeak(2);   // rampa de salida
        float muted = RenderPeak(5);
        _engine.IsGlobalMuted = false;
        RenderPeak(2);
        float unmuted = RenderPeak(5);

        Assert.Equal(0f, muted);
        Assert.Equal(0.4f, unmuted, 1e-4f);
    }

    [Fact]
    public async Task LoudMix_IsLimitedBelowFullScale()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        _engine.Microphone.SendToMonitor = true;
        _engine.Sounds.Play(Guid.NewGuid(), Constant(0.9f, 48_000), 0, loop: true);

        // Micrófono a 0,9 + sonido a 0,9 = 1,8 sin limitador.
        float peak = RenderPeak(40, micLevel: 0.9f);

        Assert.True(peak <= 0.967f, $"Pico {peak}");
        Assert.True(_engine.ReadMetrics().MonitorGainReductionDb > 5f);
        Assert.Equal(0, _engine.ReadMetrics().ClippedSamples);
    }
}
