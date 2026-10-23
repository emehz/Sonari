using Microsoft.Extensions.Logging.Abstractions;
using Sonari.Audio.Core.Devices;
using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Engine;

namespace Sonari.UnitTests.Engine;

/// <summary>Supervisor del motor con dispositivos simulados: arranque, fallo, recuperación y parada.</summary>
public sealed class AudioEngineTests : IAsyncDisposable
{
    private static readonly EngineOptions Options = new(AudioFormat.Default, 10, InputDeviceId: null, MonitorDeviceId: null);

    private readonly FakeDeviceFactory _factory = new();
    private readonly FakeWatcher _watcher = new();
    private readonly AudioEngine _engine;

    public AudioEngineTests()
    {
        _engine = new AudioEngine(
            _factory, _factory, new FakeProcessCaptureFactory(), new FakeProcessLocator(), _watcher,
            NullLogger<AudioEngine>.Instance);
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    [Fact]
    public async Task Start_OpensBothStreamsAndRuns()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        Assert.Equal(EngineState.Running, _engine.Status.State);
        Assert.Equal(SourceStatus.Active, _engine.Microphone.Status);
        Assert.Single(_factory.Captures);
        Assert.Single(_factory.Renders);
        Assert.True(_factory.Captures[0].Started);
        Assert.True(_factory.Renders[0].Started);
    }

    [Fact]
    public async Task MicrophoneAudio_ReachesTheMonitorOnlyWhenSendIsEnabled()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        FakeStream capture = _factory.Captures[0];
        FakeStream render = _factory.Renders[0];
        var output = new float[480 * 2];

        // Devuelve el pico una vez asentado: el primer bloque tras un cambio lleva la rampa antichasquidos.
        float RenderSome()
        {
            float max = 0;
            for (int i = 0; i < 20; i++)
            {
                capture.Capture(0.25f, 480);
                render.Render(output);
                if (i >= 2)
                {
                    max = MathF.Max(max, output.Max(MathF.Abs));
                }
            }

            return max;
        }

        Assert.Equal(0f, RenderSome());   // por defecto el micrófono no se oye (protección contra acoples)

        _engine.Microphone.SendToMonitor = true;
        Assert.Equal(0.25f, RenderSome(), 1e-4f);

        _engine.Microphone.IsMuted = true;
        Assert.Equal(0f, RenderSome());
    }

    [Fact]
    public async Task StreamFailure_IsRecoveredByReopening()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        _factory.Renders[0].Fail(new InvalidOperationException("AUDCLNT_E_DEVICE_INVALIDATED"));

        await Eventually.True(() => _factory.Renders.Count == 2 && _engine.Status.State == EngineState.Running);
        Assert.True(_factory.Renders[0].Disposed);
        Assert.True(_factory.Captures[0].Disposed);
        Assert.True(_factory.Captures[1].Started);
    }

    [Fact]
    public async Task UnavailableDevice_KeepsRetryingUntilItAppears()
    {
        _factory.FailOpens = 2;

        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        Assert.Equal(EngineState.Recovering, _engine.Status.State);
        Assert.Equal(SourceStatus.Waiting, _engine.Microphone.Status);

        // Un aviso de Windows provoca un reintento inmediato en vez de esperar al temporizador.
        _watcher.Raise(new EndpointChange(EndpointChangeKind.Added, "nuevo", null));

        await Eventually.True(() => _engine.Status.State == EngineState.Running);
        Assert.Equal(0, _factory.FailOpens);
    }

    [Fact]
    public async Task DefaultDeviceChange_ReopensWhenFollowingTheDefault()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        _watcher.Raise(new EndpointChange(EndpointChangeKind.DefaultChanged, "otro", EndpointFlow.Render));

        await Eventually.True(() => _factory.Renders.Count == 2 && _engine.Status.State == EngineState.Running);
    }

    [Fact]
    public async Task DefaultDeviceChange_IsIgnoredWithAnExplicitDevice()
    {
        await _engine.StartAsync(Options with { MonitorDeviceId = "auriculares" }, TestContext.Current.CancellationToken);

        _watcher.Raise(new EndpointChange(EndpointChangeKind.DefaultChanged, "otro", EndpointFlow.Render));
        await Task.Delay(600, TestContext.Current.CancellationToken);

        Assert.Single(_factory.Renders);
        Assert.Equal("auriculares", _factory.Renders[0].DeviceId);
    }

    [Fact]
    public async Task Stop_ClosesEverything()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        await _engine.StopAsync();

        Assert.Equal(EngineState.Stopped, _engine.Status.State);
        Assert.Equal(SourceStatus.Stopped, _engine.Microphone.Status);
        Assert.True(_factory.Captures[0].Disposed);
        Assert.True(_factory.Renders[0].Disposed);
        Assert.Equal(default, _engine.ReadMetrics());
    }

    [Fact]
    public async Task Controls_KeepTheirValuesAcrossRestarts()
    {
        _engine.Microphone.GainDb = -6;
        _engine.Monitor.IsMuted = true;

        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        await _engine.StopAsync();
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        Assert.Equal(-6, _engine.Microphone.GainDb);
        Assert.True(_engine.Monitor.IsMuted);
    }
}
