using Microsoft.Extensions.Logging.Abstractions;
using Sonari.Audio.Core.Effects;
using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;
using Sonari.Audio.Engine;

namespace Sonari.UnitTests.Engine;

/// <summary>Mezcla del micrófono virtual frente a la de monitoreo, con dispositivos simulados.</summary>
public sealed class VirtualMicrophoneTests : IAsyncDisposable
{
    private const string Headphones = "auriculares";
    private const string Cable = "cable";
    private static readonly EngineOptions Options = new(AudioFormat.Default, 10, null, Headphones, Cable);

    private readonly NamedRenderFactory _devices = new();
    private readonly AudioEngine _engine;

    public VirtualMicrophoneTests()
    {
        _engine = new AudioEngine(
            _devices, _devices, new FakeProcessCaptureFactory(), new FakeProcessLocator(), watcher: null,
            NullLogger<AudioEngine>.Instance);
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    private FakeStream Monitor => _devices.LastRender(Headphones)!;

    private FakeStream Virtual => _devices.LastRender(Cable)!;

    /// <summary>Hace sonar el micro y las dos salidas a la vez y devuelve el pico de cada salida (ya asentado).</summary>
    private (float Monitor, float Virtual) Run(int blocks, float micLevel)
    {
        var monitorOut = new float[960];
        var virtualOut = new float[960];
        float maxMonitor = 0, maxVirtual = 0;
        for (int i = 0; i < blocks; i++)
        {
            _devices.Captures[^1].Capture(micLevel, 480);
            Monitor.Render(monitorOut);
            Virtual.Render(virtualOut);
            if (i >= blocks / 2)
            {
                maxMonitor = MathF.Max(maxMonitor, monitorOut.Max(MathF.Abs));
                maxVirtual = MathF.Max(maxVirtual, virtualOut.Max(MathF.Abs));
            }
        }

        return (maxMonitor, maxVirtual);
    }

    private static SoundClip Constant(float value, int frames)
    {
        var samples = new float[frames * 2];
        Array.Fill(samples, value);
        return new SoundClip(samples, AudioFormat.Default, "prueba");
    }

    [Fact]
    public async Task Start_OpensTheCableAsAThirdStream()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        Assert.True(Virtual.Started);
        Assert.Equal(Cable, _engine.Status.VirtualDeviceName);
        Assert.Null(_engine.Status.VirtualMessage);
    }

    [Fact]
    public async Task Microphone_GoesToTheVirtualMicByDefaultButNotToTheMonitor()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        (float monitor, float virtualMic) = Run(20, micLevel: 0.3f);

        Assert.Equal(0f, monitor);
        Assert.Equal(0.3f, virtualMic, 1e-4f);
    }

    [Fact]
    public async Task Sends_AreIndependentPerMix()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        _engine.Microphone.SendToMonitor = true;
        _engine.Microphone.SendToVirtual = false;

        (float monitor, float virtualMic) = Run(20, micLevel: 0.3f);

        Assert.Equal(0.3f, monitor, 1e-4f);
        Assert.Equal(0f, virtualMic);
    }

    [Fact]
    public async Task VirtualVolumeAndMute_DoNotAffectTheMonitor()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        _engine.Microphone.SendToMonitor = true;
        _engine.VirtualMicrophone.GainDb = -6.0206f;

        (float monitor, float virtualMic) = Run(20, micLevel: 0.4f);
        _engine.VirtualMicrophone.IsMuted = true;
        (float monitorMuted, float virtualMuted) = Run(20, micLevel: 0.4f);

        Assert.Equal(0.4f, monitor, 1e-4f);
        Assert.Equal(0.2f, virtualMic, 1e-3f);
        Assert.Equal(0.4f, monitorMuted, 1e-4f);
        Assert.Equal(0f, virtualMuted);
    }

    [Fact]
    public async Task GlobalMute_SilencesBothMixes()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        _engine.Microphone.SendToMonitor = true;

        _engine.IsGlobalMuted = true;
        (float monitor, float virtualMic) = Run(20, micLevel: 0.4f);

        Assert.Equal(0f, monitor);
        Assert.Equal(0f, virtualMic);
    }

    [Fact]
    public async Task Sounds_ReachBothMixes()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        _engine.Sounds.Play(Guid.NewGuid(), Constant(0.25f, 96_000), 0, loop: true);

        (float monitor, float virtualMic) = Run(40, micLevel: 0f);

        Assert.Equal(0.25f, monitor, 1e-3f);
        Assert.Equal(0.25f, virtualMic, 1e-3f);
    }

    [Fact]
    public async Task Sounds_KeepReachingTheVirtualMicWhenTheHeadphonesStop()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        _engine.Sounds.Play(Guid.NewGuid(), Constant(0.25f, 96_000), 0, loop: true);

        // Solo se llama al render del cable: los auriculares no piden audio (desconectados o colgados).
        var output = new float[960];
        float max = 0;
        for (int i = 0; i < 20; i++)
        {
            Virtual.Render(output);
            if (i > 2)
            {
                max = MathF.Max(max, output.Max(MathF.Abs));
            }
        }

        Assert.Equal(0.25f, max, 1e-3f);
    }

    [Fact]
    public async Task MissingCable_DoesNotStopMicrophoneOrMonitor_AndIsRetried()
    {
        _devices.Unavailable.Add(Cable);

        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        Assert.Equal(EngineState.Running, _engine.Status.State);
        Assert.Contains("no disponible", _engine.Status.VirtualMessage);
        Assert.Null(_devices.LastRender(Cable));

        _devices.Unavailable.Remove(Cable);
        await Eventually.True(() => _engine.Status.VirtualDeviceName == Cable);
        Assert.Single(_devices.Captures);   // el micrófono no se reabrió
    }

    [Fact]
    public async Task CableFailure_IsRecoveredWithoutTouchingTheMonitor()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        FakeStream firstCable = Virtual;
        FakeStream monitor = Monitor;

        firstCable.Fail(new InvalidOperationException("cable desinstalado"));

        await Eventually.True(() => _devices.LastRender(Cable) != firstCable && _engine.Status.VirtualDeviceName == Cable);
        Assert.Same(monitor, Monitor);
        Assert.False(monitor.Disposed);
    }

    [Fact]
    public async Task MonitorAndVirtualOnTheSameDevice_IsRejected()
    {
        await _engine.StartAsync(Options with { MonitorDeviceId = Cable }, TestContext.Current.CancellationToken);

        Assert.Equal(EngineState.Running, _engine.Status.State);
        Assert.Null(_engine.Status.VirtualDeviceName);
        Assert.Contains("mismo dispositivo", _engine.Status.VirtualMessage);
    }

    [Fact]
    public async Task WithoutCable_NoThirdStreamIsOpened()
    {
        await _engine.StartAsync(Options with { VirtualOutputDeviceId = null }, TestContext.Current.CancellationToken);

        Assert.Single(_devices.Renders);
        Assert.Equal("Micrófono virtual desactivado.", _engine.Status.VirtualMessage);
    }

    [Fact]
    public async Task VoiceEffects_ProcessTheMicrophoneOnceForBothMixes()
    {
        _engine.VoiceEffects = new VoiceEffectSettings { Limiter = new() { Enabled = true, CeilingDb = -12 } };
        _engine.Microphone.SendToMonitor = true;
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        (float monitor, float virtualMic) = Run(40, micLevel: 0.9f);

        float ceiling = Decibels.ToLinear(-12);
        Assert.Equal(ceiling, monitor, 1e-3f);
        Assert.Equal(ceiling, virtualMic, 1e-3f);
    }

    [Fact]
    public async Task VoiceEffects_ChangeWhileRunning()
    {
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);
        Assert.Equal(0.3f, Run(20, micLevel: 0.3f).Virtual, 1e-4f);

        _engine.VoiceEffects = new VoiceEffectSettings { Limiter = new() { Enabled = true, CeilingDb = -20 } };

        Assert.Equal(0.1f, Run(40, micLevel: 0.3f).Virtual, 1e-3f);
    }

    [Fact]
    public async Task VoiceEffects_MasterSwitchOff_LeavesTheVoiceUntouched()
    {
        _engine.VoiceEffects = new VoiceEffectSettings { Enabled = false, Limiter = new() { Enabled = true, CeilingDb = -20 } };
        await _engine.StartAsync(Options, TestContext.Current.CancellationToken);

        Assert.Equal(0.3f, Run(20, micLevel: 0.3f).Virtual, 1e-4f);
    }

    /// <summary>Fábrica que nombra cada salida por su id, para distinguir auriculares y cable.</summary>
    private sealed class NamedRenderFactory : ICaptureStreamFactory, IRenderStreamFactory
    {
        public List<FakeStream> Captures { get; } = [];

        public List<FakeStream> Renders { get; } = [];

        public HashSet<string> Unavailable { get; } = [];

        public FakeStream? LastRender(string id)
        {
            lock (Renders)
            {
                return Renders.LastOrDefault(r => r.DeviceId == id);
            }
        }

        public IAudioStream Open(string? deviceId, AudioFormat format, int bufferMilliseconds, CaptureDataHandler onData)
        {
            var stream = new FakeStream(deviceId, format) { OnCapture = onData };
            lock (Captures)
            {
                Captures.Add(stream);
            }

            return stream;
        }

        public IAudioStream Open(string? deviceId, AudioFormat format, int latencyMilliseconds, RenderHandler render)
        {
            lock (Unavailable)
            {
                if (deviceId is not null && Unavailable.Contains(deviceId))
                {
                    throw new AudioDeviceUnavailableException("No se encontró el dispositivo de salida seleccionado.");
                }
            }

            var stream = new FakeStream(deviceId, format) { OnRender = render };
            lock (Renders)
            {
                Renders.Add(stream);
            }

            return stream;
        }
    }
}
