using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Devices;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Infrastructure.Settings;

namespace Sonari.Presentation.ViewModels;

/// <param name="Id"><c>null</c> = seguir al predeterminado de Windows.</param>
public sealed record DeviceOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Mezclador: una tira por fuente (micrófono, aplicaciones, sistema) y el bus de monitoreo, con medidores y
/// diagnóstico. Los envíos al micrófono virtual llegarán en la Fase 5.
/// </summary>
public sealed partial class MixerViewModel : PageViewModel
{
    private static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(33);

    private readonly IAudioEngine _engine;
    private readonly IAudioDeviceCatalog _catalog;
    private readonly ISettingsService _settings;
    private readonly ILogger<MixerViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;
    private readonly DispatcherTimer _meterTimer;
    private readonly MeterBallistics _monitorMeter = new();
    private DateTime _lastMeterTick = DateTime.UtcNow;
    private bool _devicesLoaded;
    private bool _suppressDeviceChange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsStopped), nameof(EngineButtonText))]
    private EngineState _engineState = EngineState.Stopped;

    [ObservableProperty]
    private string _statusMessage = EngineStatus.Stopped.Message;

    [ObservableProperty]
    private string? _deviceSummary;

    [ObservableProperty]
    private DeviceOption? _selectedInput;

    [ObservableProperty]
    private DeviceOption? _selectedMonitor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorGainText))]
    private double _monitorGainDb;

    [ObservableProperty]
    private bool _monitorMuted;

    [ObservableProperty]
    private double _monitorLevelDb = Decibels.Floor;

    [ObservableProperty]
    private double _monitorPeakDb = Decibels.Floor;

    [ObservableProperty]
    private long _resyncs;

    [ObservableProperty]
    private long _clippedSamples;

    [ObservableProperty]
    private bool _hasDuplicationRisk;

    [ObservableProperty]
    private bool _isGlobalMuted;

    [ObservableProperty]
    private string _limiterText = "0,0 dB";

    public MixerViewModel(
        IAudioEngine engine,
        IAudioDeviceCatalog catalog,
        ISettingsService settings,
        ILogger<MixerViewModel> logger)
        : base("mixer", "Mezclador", "\uE9E9",
            "Cada fuente es un canal independiente. El envío al micrófono virtual llegará en la Fase 5.")
    {
        _engine = engine;
        _catalog = catalog;
        _settings = settings;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        MonitorGainDb = engine.Monitor.GainDb;
        MonitorMuted = engine.Monitor.IsMuted;
        IsGlobalMuted = engine.IsGlobalMuted;

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = MeterInterval };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        _engine.StatusChanged += (_, status) => PostToUi(() => ApplyStatus(status));
        _engine.SourcesChanged += (_, _) => PostToUi(SyncChannels);
        SyncChannels();
        ApplyStatus(_engine.Status);
    }

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];

    public ObservableCollection<DeviceOption> InputDevices { get; } = [];

    public ObservableCollection<DeviceOption> MonitorDevices { get; } = [];

    public bool IsRunning => EngineState != EngineState.Stopped;

    public bool IsStopped => EngineState == EngineState.Stopped;

    public string EngineButtonText => IsRunning ? "Detener motor" : "Iniciar motor";

    public string MonitorGainText => MonitorGainDb <= -60 ? "−∞ dB" : $"{MonitorGainDb:+0.0;−0.0;0.0} dB";

    public double MinGainDb => -60;

    public double MaxGainDb => 12;

    public override async Task OnNavigatedToAsync()
    {
        if (!_devicesLoaded)
        {
            await RefreshDevicesAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleEngineAsync()
    {
        try
        {
            if (IsRunning)
            {
                await _engine.StopAsync();
            }
            else
            {
                await _engine.StartAsync(BuildOptions());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al cambiar el estado del motor");
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveChannel(ChannelViewModel? channel)
    {
        if (channel is { CanRemove: true })
        {
            _engine.RemoveSource(channel.Source);
        }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        try
        {
            var (inputs, outputs) = await Task.Run(() => (
                _catalog.GetEndpoints(EndpointFlow.Capture),
                _catalog.GetEndpoints(EndpointFlow.Render)));

            AudioSettings audio = _settings.Current.Audio;
            _suppressDeviceChange = true;
            try
            {
                Fill(InputDevices, inputs);
                Fill(MonitorDevices, outputs);
                SelectedInput = Find(InputDevices, audio.InputDeviceId);
                SelectedMonitor = Find(MonitorDevices, audio.MonitorDeviceId);
            }
            finally
            {
                _suppressDeviceChange = false;
            }

            _devicesLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al enumerar dispositivos para el mezclador");
            StatusMessage = $"No se pudieron leer los dispositivos: {ex.Message}";
        }
    }

    partial void OnMonitorGainDbChanged(double value) => _engine.Monitor.GainDb = (float)value;

    partial void OnMonitorMutedChanged(bool value) => _engine.Monitor.IsMuted = value;

    partial void OnIsGlobalMutedChanged(bool value) => _engine.IsGlobalMuted = value;

    partial void OnSelectedInputChanged(DeviceOption? value) => _ = OnDeviceSelectionChangedAsync();

    partial void OnSelectedMonitorChanged(DeviceOption? value) => _ = OnDeviceSelectionChangedAsync();

    private async Task OnDeviceSelectionChangedAsync()
    {
        if (_suppressDeviceChange || SelectedInput is null || SelectedMonitor is null)
        {
            return;
        }

        try
        {
            AppSettings current = _settings.Current;
            AudioSettings audio = current.Audio with
            {
                InputDeviceId = SelectedInput.Id,
                MonitorDeviceId = SelectedMonitor.Id,
            };

            if (audio != current.Audio)
            {
                await _settings.SaveAsync(current with { Audio = audio });
            }

            // Cambiar de dispositivo reabre micrófono y salida; las fuentes de aplicación se reabren también (corte breve).
            if (IsRunning)
            {
                await _engine.StartAsync(BuildOptions());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al aplicar el cambio de dispositivo");
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private EngineOptions BuildOptions()
    {
        AudioSettings audio = _settings.Current.Audio;
        return new EngineOptions(
            new AudioFormat(audio.SampleRate, 2),
            audio.BufferMilliseconds,
            audio.InputDeviceId,
            audio.MonitorDeviceId);
    }

    private void SyncChannels()
    {
        IReadOnlyList<ISourceChannel> sources = _engine.Sources;
        for (int i = Channels.Count - 1; i >= 0; i--)
        {
            if (!sources.Any(s => s.Id == Channels[i].Id))
            {
                Channels.RemoveAt(i);
            }
        }

        foreach (ISourceChannel source in sources)
        {
            ChannelViewModel? existing = Channels.FirstOrDefault(c => c.Id == source.Id);
            if (existing is null)
            {
                Channels.Add(new ChannelViewModel(source));
            }
            else
            {
                existing.RefreshStatus();
            }
        }

        HasDuplicationRisk = sources.Any(s => s.Kind == SourceKind.SystemAudio) &&
            sources.Any(s => s.Kind == SourceKind.Application);
        IsGlobalMuted = _engine.IsGlobalMuted;
    }

    private void ApplyStatus(EngineStatus status)
    {
        EngineState = status.State;
        StatusMessage = status.Message;
        DeviceSummary = status.State == EngineState.Running
            ? $"{status.InputDeviceName} → {status.MonitorDeviceName} · buffer de salida {status.OutputLatencyMilliseconds:0} ms"
            : null;

        if (status.State == EngineState.Stopped)
        {
            _meterTimer.Stop();
            _monitorMeter.Reset();
            MonitorLevelDb = _monitorMeter.LevelDb;
            MonitorPeakDb = _monitorMeter.PeakDb;
            foreach (ChannelViewModel channel in Channels)
            {
                channel.ResetMeters();
            }
        }
        else if (!_meterTimer.IsEnabled)
        {
            _lastMeterTick = DateTime.UtcNow;
            _meterTimer.Start();
        }
    }

    private void UpdateMeters()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan elapsed = now - _lastMeterTick;
        _lastMeterTick = now;

        foreach (ChannelViewModel channel in Channels)
        {
            channel.UpdateMeters(elapsed, now);
        }

        EngineMetrics metrics = _engine.ReadMetrics();
        _monitorMeter.Update(metrics.MonitorOutput.Max, elapsed, now);
        MonitorLevelDb = _monitorMeter.LevelDb;
        MonitorPeakDb = _monitorMeter.PeakDb;
        Resyncs = metrics.Resyncs;
        ClippedSamples = metrics.ClippedSamples;
        LimiterText = metrics.MonitorGainReductionDb > 0.05f ? $"−{metrics.MonitorGainReductionDb:0.0} dB" : "0,0 dB";
    }

    private void PostToUi(Action action)
    {
        if (_uiContext is null)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    private static void Fill(ObservableCollection<DeviceOption> target, IEnumerable<AudioEndpointInfo> endpoints)
    {
        target.Clear();
        target.Add(new DeviceOption(null, "Predeterminado de Windows"));
        foreach (AudioEndpointInfo endpoint in endpoints)
        {
            target.Add(new DeviceOption(endpoint.Id, endpoint.Name));
        }
    }

    private static DeviceOption Find(ObservableCollection<DeviceOption> options, string? id) =>
        options.FirstOrDefault(o => o.Id == id) ?? options[0];
}
