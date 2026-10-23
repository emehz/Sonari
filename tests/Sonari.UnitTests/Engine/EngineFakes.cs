using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Formats;

namespace Sonari.UnitTests.Engine;

/// <summary>Dispositivos y procesos simulados para probar el motor sin hardware.</summary>
internal sealed class FakeDeviceFactory : ICaptureStreamFactory, IRenderStreamFactory
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
        if (Interlocked.Decrement(ref _failOpens) >= 0)
        {
            throw new AudioDeviceUnavailableException("Micrófono desconectado.");
        }

        Interlocked.Exchange(ref _failOpens, 0);
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
}

internal sealed class FakeProcessCaptureFactory : IProcessCaptureStreamFactory
{
    public bool IsSupported { get; set; } = true;

    public List<(int ProcessId, bool Include, FakeStream Stream)> Opened { get; } = [];

    public FakeStream? Last
    {
        get
        {
            lock (Opened)
            {
                return Opened.Count == 0 ? null : Opened[^1].Stream;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (Opened)
            {
                return Opened.Count;
            }
        }
    }

    public Task<IAudioStream> OpenAsync(
        int processId, bool includeProcessTree, AudioFormat format, CaptureDataHandler onData, CancellationToken cancellationToken = default)
    {
        var stream = new FakeStream($"pid:{processId}", format) { OnCapture = onData };
        lock (Opened)
        {
            Opened.Add((processId, includeProcessTree, stream));
        }

        return Task.FromResult<IAudioStream>(stream);
    }
}

internal sealed class FakeProcessLocator : IProcessLocator
{
    private readonly Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);

    public int CurrentProcessId => 4242;

    public void Launch(string path, int processId)
    {
        lock (_running)
        {
            _running[path] = processId;
        }
    }

    public void Exit(string path)
    {
        lock (_running)
        {
            _running.Remove(path);
        }
    }

    public int? FindRootProcess(string executablePath, int? preferredProcessId)
    {
        lock (_running)
        {
            return _running.TryGetValue(executablePath, out int pid) ? pid : null;
        }
    }

    public bool IsRunning(int processId)
    {
        lock (_running)
        {
            return _running.ContainsValue(processId);
        }
    }
}

internal sealed class FakeStream(string? deviceId, AudioFormat format) : IAudioStream
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

internal sealed class FakeWatcher : IAudioDeviceWatcher
{
    public event EventHandler<EndpointChange>? Changed;

    public void Raise(EndpointChange change) => Changed?.Invoke(this, change);
}

internal static class Eventually
{
    public static async Task True(Func<bool> condition, int seconds = 8)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("La condición no se cumplió a tiempo.");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
