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

    private readonly FakeFactory _factory = new();
    private readonly FakeWatcher _watcher = new();
    private readonly AudioEngine _engine;

    public AudioEngineTests()
    {
        _engine = new AudioEngine(_factory, _factory, _watcher, NullLogger<AudioEngine>.Instance);
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    [Fact]
    public async Task Start_OpensBothStreamsAndRuns()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        Assert.Equal(EngineState.Running, _engine.Status.State);
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

        await WaitUntil(() => _factory.Renders.Count == 2 && _engine.Status.State == EngineState.Running);
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

        // Un aviso de Windows provoca un reintento inmediato en vez de esperar al temporizador.
        _watcher.Raise(new EndpointChange(EndpointChangeKind.Added, "nuevo", null));

        await WaitUntil(() => _engine.Status.State == EngineState.Running);
        Assert.Equal(0, _factory.FailOpens);
    }

    [Fact]
    public async Task DefaultDeviceChange_ReopensWhenFollowingTheDefault()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        _watcher.Raise(new EndpointChange(EndpointChangeKind.DefaultChanged, "otro", EndpointFlow.Render));

        await WaitUntil(() => _factory.Renders.Count == 2 && _engine.Status.State == EngineState.Running);
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

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("La condición no se cumplió a tiempo.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FakeFactory : ICaptureStreamFactory, IRenderStreamFactory
    {
        private int _failOpens;

        public List<FakeStream> Captures { get; } = [];

        public List<FakeStream> Renders { get; } = [];

        public int FailOpens
        {
            get => Volatile.Read(ref _failOpens);
            set => Volatile.Write(ref _failOpens, value);
        }

        public IAudioStream Open(string? deviceId, AudioFormat format, int bufferMilliseconds, CaptureDataHandler onData)
        {
            ThrowIfConfiguredToFail();
            var stream = new FakeStream(deviceId, format) { OnCapture = onData };
            lock (Captures)
            {
                Captures.Add(stream);
            }

            return stream;
        }

        public IAudioStream Open(string? deviceId, AudioFormat format, int latencyMilliseconds, RenderHandler render)
        {
            var stream = new FakeStream(deviceId, format) { OnRender = render };
            lock (Renders)
            {
                Renders.Add(stream);
            }

            return stream;
        }

        private void ThrowIfConfiguredToFail()
        {
            if (Interlocked.Decrement(ref _failOpens) >= 0)
            {
                throw new AudioDeviceUnavailableException("Micrófono desconectado.");
            }

            Interlocked.Exchange(ref _failOpens, 0);
        }
    }

    private sealed class FakeStream(string? deviceId, AudioFormat format) : IAudioStream
    {
        public CaptureDataHandler? OnCapture { get; init; }

        public RenderHandler? OnRender { get; init; }

        public string? DeviceId { get; } = deviceId;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public AudioFormat Format { get; } = format;

        public string DeviceName => DeviceId ?? "Predeterminado";

        public double ReportedLatencyMilliseconds => 10;

        public event Action<Exception?>? Stopped;

        public void Start() => Started = true;

        public void Stop() => Stopped?.Invoke(null);

        public void Dispose()
        {
            Disposed = true;
            Stopped?.Invoke(null);
        }

        public void Fail(Exception ex) => Stopped?.Invoke(ex);

        public void Capture(float value, int frames)
        {
            var data = new float[frames * Format.Channels];
            Array.Fill(data, value);
            OnCapture!(data, discontinuity: false);
        }

        public void Render(float[] output) => OnRender!(output);
    }

    private sealed class FakeWatcher : IAudioDeviceWatcher
    {
        public event EventHandler<EndpointChange>? Changed;

        public void Raise(EndpointChange change) => Changed?.Invoke(this, change);
    }
}
