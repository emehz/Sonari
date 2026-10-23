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
/// Mezclador, versión de la Fase 2: un canal (micrófono) y el bus de monitoreo, con medidores y diagnóstico.
/// Los canales de aplicaciones, sistema y sonidos se añadirán aquí en las fases 3 y 4.
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
    private readonly MeterBallistics _micMeter = new();
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
    [NotifyPropertyChangedFor(nameof(MicGainText))]
    private double _micGainDb;

    [ObservableProperty]
    private bool _micMuted;

    [ObservableProperty]
    private bool _micToMonitor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorGainText))]
    private double _monitorGainDb;

    [ObservableProperty]
    private bool _monitorMuted;

    [ObservableProperty]
    private double _micLevelDb = Decibels.Floor;

    [ObservableProperty]
    private double _micPeakDb = Decibels.Floor;

    [ObservableProperty]
    private double _monitorLevelDb = Decibels.Floor;

    [ObservableProperty]
    private double _monitorPeakDb = Decibels.Floor;

    [ObservableProperty]
    private string _bufferText = "—";

    [ObservableProperty]
    private string _driftText = "—";

    [ObservableProperty]
    private long _underruns;

    [ObservableProperty]
    private long _overrunSamples;

    [ObservableProperty]
    private long _resyncs;

    [ObservableProperty]
    private long _discontinuities;

    [ObservableProperty]
    private long _clippedSamples;

    public MixerViewModel(
        IAudioEngine engine,
        IAudioDeviceCatalog catalog,
        ISettingsService settings,
        ILogger<MixerViewModel> logger)
        : base("mixer", "Mezclador", "",
            "Micrófono → auriculares con compensación de deriva. Las demás fuentes llegarán en las fases 3 y 4.")
    {
        _engine = engine;
        _catalog = catalog;
        _settings = settings;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        MicGainDb = engine.Microphone.GainDb;
        MicMuted = engine.Microphone.IsMuted;
        MicToMonitor = engine.Microphone.SendToMonitor;
        MonitorGainDb = engine.Monitor.GainDb;
        MonitorMuted = engine.Monitor.IsMuted;

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = MeterInterval };
        _meterTimer.Tick += (_, _) => UpdateMeters();

        _engine.StatusChanged += OnEngineStatusChanged;
        ApplyStatus(_engine.Status);
    }

    public ObservableCollection<DeviceOption> InputDevices { get; } = [];

    public ObservableCollection<DeviceOption> MonitorDevices { get; } = [];

    public bool IsRunning => EngineState != EngineState.Stopped;

    public bool IsStopped => EngineState == EngineState.Stopped;

    public string EngineButtonText => IsRunning ? "Detener motor" : "Iniciar motor";

    public string MicGainText => FormatGain(MicGainDb);

    public string MonitorGainText => FormatGain(MonitorGainDb);

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

    partial void OnMicGainDbChanged(double value) => _engine.Microphone.GainDb = (float)value;

    partial void OnMicMutedChanged(bool value) => _engine.Microphone.IsMuted = value;

    partial void OnMicToMonitorChanged(bool value) => _engine.Microphone.SendToMonitor = value;

    partial void OnMonitorGainDbChanged(double value) => _engine.Monitor.GainDb = (float)value;

    partial void OnMonitorMutedChanged(bool value) => _engine.Monitor.IsMuted = value;

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

            // Fase 2: cambiar de dispositivo reinicia el motor completo (un corte breve). Más adelante se reabrirá solo ese stream.
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

    private void OnEngineStatusChanged(object? sender, EngineStatus status)
    {
        if (_uiContext is null)
        {
            ApplyStatus(status);
        }
        else
        {
            _uiContext.Post(_ => ApplyStatus(status), null);
        }
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
            _micMeter.Reset();
            _monitorMeter.Reset();
            UpdateMeterProperties();
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

        EngineMetrics metrics = _engine.ReadMetrics();
        _micMeter.Update(metrics.MicrophoneInput.Max, elapsed, now);
        _monitorMeter.Update(metrics.MonitorOutput.Max, elapsed, now);
        UpdateMeterProperties();

        BufferText = metrics.MonitorBufferMilliseconds > 0 ? $"{metrics.MonitorBufferMilliseconds:0.0} ms" : "cebando…";
        DriftText = $"{metrics.MonitorDriftPpm:+0;-0;0} ppm";
        Underruns = metrics.Underruns;
        OverrunSamples = metrics.OverrunSamples;
        Resyncs = metrics.Resyncs;
        Discontinuities = metrics.CaptureDiscontinuities;
        ClippedSamples = metrics.ClippedSamples;
    }

    private void UpdateMeterProperties()
    {
        MicLevelDb = _micMeter.LevelDb;
        MicPeakDb = _micMeter.PeakDb;
        MonitorLevelDb = _monitorMeter.LevelDb;
        MonitorPeakDb = _monitorMeter.PeakDb;
    }

    private static string FormatGain(double db) => db <= -60 ? "−∞ dB" : $"{db:+0.0;−0.0;0.0} dB";

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
