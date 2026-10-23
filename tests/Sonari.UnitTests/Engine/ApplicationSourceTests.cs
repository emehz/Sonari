using Microsoft.Extensions.Logging.Abstractions;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Engine;

namespace Sonari.UnitTests.Engine;

/// <summary>Fuentes de aplicación y de sistema con procesos simulados.</summary>
public sealed class ApplicationSourceTests : IAsyncDisposable
{
    private const string Browser = @"C:\Program Files\Navegador\navegador.exe";
    private static readonly EngineOptions Options = new(AudioFormat.Default, 10, InputDeviceId: null, MonitorDeviceId: null);

    private readonly FakeDeviceFactory _devices = new();
    private readonly FakeProcessCaptureFactory _processCapture = new();
    private readonly FakeProcessLocator _processes = new();
    private readonly AudioEngine _engine;

    public ApplicationSourceTests()
    {
        _engine = new AudioEngine(
            _devices, _devices, _processCapture, _processes, watcher: null, NullLogger<AudioEngine>.Instance);
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    [Fact]
    public async Task AddedWhileStopped_StartsCapturingWhenTheEngineStarts()
    {
        _processes.Launch(Browser, 100);
        ISourceChannel source = _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador"));
        Assert.Equal(SourceStatus.Stopped, source.Status);
        Assert.Equal([SourceKind.Microphone, SourceKind.SoundPlayer, SourceKind.Application], _engine.Sources.Select(s => s.Kind));

        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        await Eventually.True(() => source.Status == SourceStatus.Active);
        Assert.Equal(100, source.ProcessId);
        Assert.Equal((100, true), (_processCapture.Opened[0].ProcessId, _processCapture.Opened[0].Include));
        Assert.False(source.SendToMonitor);   // ya la oyes directamente: no se duplica por defecto
    }

    [Fact]
    public async Task AddedWhileRunning_DoesNotRestartTheMicrophone()
    {
        _processes.Launch(Browser, 100);
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        ISourceChannel source = _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador"));

        await Eventually.True(() => source.Status == SourceStatus.Active);
        Assert.Single(_devices.Captures);
        Assert.False(_devices.Captures[0].Disposed);
    }

    [Fact]
    public async Task ApplicationAudio_IsMixedIntoTheMonitorWhenSent()
    {
        _processes.Launch(Browser, 100);
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        ISourceChannel source = _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador"));
        await Eventually.True(() => _processCapture.Last is not null);
        source.SendToMonitor = true;

        var output = new float[480 * 2];
        float max = 0;
        for (int i = 0; i < 20; i++)
        {
            _processCapture.Last!.Capture(0.3f, 480);
            _devices.Captures[0].Capture(0f, 480);
            _devices.Renders[0].Render(output);
            if (i >= 3)
            {
                max = MathF.Max(max, output.Max(MathF.Abs));
            }
        }

        Assert.Equal(0.3f, max, 1e-4f);
        Assert.True(source.ReadMetrics().Input.Max > 0.29f);
    }

    [Fact]
    public async Task WhenTheProcessExits_WaitsAndReattachesToTheNewInstance()
    {
        _processes.Launch(Browser, 100);
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        ISourceChannel source = _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador"));
        await Eventually.True(() => source.Status == SourceStatus.Active);
        FakeStream first = _processCapture.Last!;

        _processes.Exit(Browser);
        await Eventually.True(() => source.Status == SourceStatus.Waiting);
        Assert.True(first.Disposed);

        _processes.Launch(Browser, 200);
        await Eventually.True(() => source.Status == SourceStatus.Active && source.ProcessId == 200);
        Assert.Equal(200, _processCapture.Opened[^1].ProcessId);
    }

    [Fact]
    public async Task CaptureFailure_IsRetried()
    {
        _processes.Launch(Browser, 100);
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        ISourceChannel source = _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador"));
        await Eventually.True(() => source.Status == SourceStatus.Active);

        _processCapture.Last!.Fail(new InvalidOperationException("se perdió el stream"));

        await Eventually.True(() => source.Status == SourceStatus.Faulted);
        await Eventually.True(() => _processCapture.Count == 2 && source.Status == SourceStatus.Active);
    }

    [Fact]
    public async Task SystemAudio_ExcludesOwnProcessAndIsUnique()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        ISourceChannel system = _engine.AddSystemAudioSource();
        ISourceChannel again = _engine.AddSystemAudioSource();

        Assert.Same(system, again);
        await Eventually.True(() => system.Status == SourceStatus.Active);
        Assert.Equal((_processes.CurrentProcessId, false), (_processCapture.Opened[0].ProcessId, _processCapture.Opened[0].Include));
    }

    [Fact]
    public async Task RemoveSource_ReleasesItsCaptureAndKeepsTheRest()
    {
        _processes.Launch(Browser, 100);
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        ISourceChannel source = _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador"));
        await Eventually.True(() => source.Status == SourceStatus.Active);

        _engine.RemoveSource(source);

        await Eventually.True(() => _processCapture.Last!.Disposed);
        Assert.DoesNotContain(_engine.Sources, s => s.Id == source.Id);
        Assert.False(_devices.Captures[0].Disposed);
        Assert.Equal(EngineState.Running, _engine.Status.State);
    }

    [Fact]
    public async Task MonitorDeviceFailure_DoesNotInterruptApplicationCapture()
    {
        _processes.Launch(Browser, 100);
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        ISourceChannel source = _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador"));
        await Eventually.True(() => source.Status == SourceStatus.Active);

        _devices.Renders[0].Fail(new InvalidOperationException("auriculares desconectados"));
        await Eventually.True(() => _devices.Renders.Count == 2 && _engine.Status.State == EngineState.Running);

        Assert.Equal(1, _processCapture.Count);
        Assert.False(_processCapture.Last!.Disposed);
    }

    [Fact]
    public async Task Stop_ReleasesApplicationCaptures()
    {
        _processes.Launch(Browser, 100);
        ISourceChannel source = _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador"));
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        await Eventually.True(() => source.Status == SourceStatus.Active);

        await _engine.StopAsync();

        Assert.True(_processCapture.Last!.Disposed);
        Assert.Equal(SourceStatus.Stopped, source.Status);
        Assert.Contains(_engine.Sources, s => s.Id == source.Id);   // la fuente sigue en la lista para el próximo arranque
    }

    [Fact]
    public void Microphone_CannotBeRemoved()
    {
        Assert.Throws<InvalidOperationException>(() => _engine.RemoveSource(_engine.Microphone));
    }

    [Fact]
    public void WithoutProcessLoopbackSupport_AddingApplicationsFails()
    {
        _processCapture.IsSupported = false;

        Assert.False(_engine.SupportsProcessCapture);
        Assert.Throws<NotSupportedException>(() => _engine.AddApplicationSource(new ApplicationTarget(Browser, "Navegador")));
        Assert.Throws<NotSupportedException>(() => _engine.AddSystemAudioSource());
    }
}
