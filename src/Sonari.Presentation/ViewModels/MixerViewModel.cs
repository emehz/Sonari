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
/// Mezclador: una tira por fuente (micrófono, aplicaciones, sistema) con envíos al micrófono virtual y al monitoreo,
/// los dispositivos de cada mezcla, medidores y diagnóstico.
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
    private readonly MeterBallistics _virtualMeter = new();
    private IReadOnlyList<AudioEndpointInfo> _inputEndpoints = [];
    private IReadOnlyList<AudioEndpointInfo> _outputEndpoints = [];
    private DateTime _lastMeterTick = DateTime.UtcNow;
    private bool _devicesLoaded;
    private bool _suppressDeviceChange;
    private bool _autoSelectingVirtual;
    private bool _virtualChosenByUser;

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
    private DeviceOption? _selectedVirtual;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VirtualGainText))]
    private double _virtualGainDb;

    [ObservableProperty]
    private bool _virtualMuted;

    [ObservableProperty]
    private double _virtualLevelDb = Decibels.Floor;

    [ObservableProperty]
    private double _virtualPeakDb = Decibels.Floor;

    [ObservableProperty]
    private string _virtualLimiterText = "0,0 dB";

    [ObservableProperty]
    private bool _isVirtualActive;

    [ObservableProperty]
    private string _virtualStatusText = "Micrófono virtual desactivado.";

    /// <summary>Qué entrada hay que elegir en Discord u otras aplicaciones.</summary>
    [ObservableProperty]
    private string? _discordHint;

    /// <summary>Configuración peligrosa (realimentación): el micrófono virtual no se activa mientras exista.</summary>
    [ObservableProperty]
    private string? _routingError;

    [ObservableProperty]
    private string? _routingWarning;

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
            "Cada fuente es un canal con envíos independientes al micrófono virtual (lo que oyen los demás) y al monitoreo (lo que oyes tú).")
    {
        _engine = engine;
        _catalog = catalog;
        _settings = settings;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        MonitorGainDb = engine.Monitor.GainDb;
        MonitorMuted = engine.Monitor.IsMuted;
        VirtualGainDb = engine.VirtualMicrophone.GainDb;
        VirtualMuted = engine.VirtualMicrophone.IsMuted;
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

    public ObservableCollection<DeviceOption> VirtualDevices { get; } = [];

    public string VirtualGainText => VirtualGainDb <= -60 ? "−∞ dB" : $"{VirtualGainDb:+0.0;−0.0;0.0} dB";

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
            _inputEndpoints = inputs;
            _outputEndpoints = outputs;
            _suppressDeviceChange = true;
            try
            {
                Fill(InputDevices, inputs);
                Fill(MonitorDevices, outputs);
                FillVirtual(VirtualDevices, outputs);
                SelectedInput = Find(InputDevices, audio.InputDeviceId);
                SelectedMonitor = Find(MonitorDevices, audio.MonitorDeviceId);
                SelectedVirtual = Find(VirtualDevices, audio.VirtualOutputDeviceId);
            }
            finally
            {
                _suppressDeviceChange = false;
            }

            // Con el driver de Sonari instalado, el micrófono virtual lo usa sin que haya que elegirlo, salvo que el
            // usuario ya haya elegido otra cosa. Esto lo guarda en los ajustes como cualquier otro cambio.
            AudioEndpointInfo? sonariOutput = SonariVirtualDevice.AutoSelectOutput(
                outputs, audio.VirtualOutputDeviceId, audio.VirtualOutputChosen);
            if (sonariOutput is not null)
            {
                _logger.LogInformation("Micrófono virtual: se elige el driver de Sonari ({Name})", sonariOutput.Name);
                _autoSelectingVirtual = true;
                try
                {
                    SelectedVirtual = Find(VirtualDevices, sonariOutput.Id);
                }
                finally
                {
                    _autoSelectingVirtual = false;
                }
            }

            UpdateRoutingChecks();

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

    partial void OnSelectedVirtualChanged(DeviceOption? value)
    {
        if (!_suppressDeviceChange && !_autoSelectingVirtual)
        {
            _virtualChosenByUser = true;
        }

        _ = OnDeviceSelectionChangedAsync();
    }

    partial void OnVirtualGainDbChanged(double value) => _engine.VirtualMicrophone.GainDb = (float)value;

    partial void OnVirtualMutedChanged(bool value) => _engine.VirtualMicrophone.IsMuted = value;

    private async Task OnDeviceSelectionChangedAsync()
    {
        if (_suppressDeviceChange || SelectedInput is null || SelectedMonitor is null || SelectedVirtual is null)
        {
            return;
        }

        UpdateRoutingChecks();

        try
        {
            AppSettings current = _settings.Current;
            AudioSettings audio = current.Audio with
            {
                InputDeviceId = SelectedInput.Id,
                MonitorDeviceId = SelectedMonitor.Id,
                VirtualOutputDeviceId = SelectedVirtual.Id,
                VirtualOutputChosen = current.Audio.VirtualOutputChosen || _virtualChosenByUser,
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
            audio.MonitorDeviceId,

            // Con una configuración que se realimentaría, el motor arranca sin micrófono virtual.
            RoutingError is null ? audio.VirtualOutputDeviceId : null);
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
        IsVirtualActive = status.VirtualDeviceName is not null;
        VirtualStatusText = status.State == EngineState.Stopped
            ? (SelectedVirtual?.Id is null ? "Micrófono virtual desactivado." : "Se activará al iniciar el motor.")
            : status.VirtualDeviceName is not null
                ? $"Enviando a «{status.VirtualDeviceName}»"
                : RoutingError ?? status.VirtualMessage ?? "Micrófono virtual desactivado.";

        if (status.State == EngineState.Stopped)
        {
            _meterTimer.Stop();
            _monitorMeter.Reset();
            _virtualMeter.Reset();
            MonitorLevelDb = _monitorMeter.LevelDb;
            MonitorPeakDb = _monitorMeter.PeakDb;
            VirtualLevelDb = _virtualMeter.LevelDb;
            VirtualPeakDb = _virtualMeter.PeakDb;
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
        _virtualMeter.Update(metrics.VirtualOutput.Max, elapsed, now);
        VirtualLevelDb = _virtualMeter.LevelDb;
        VirtualPeakDb = _virtualMeter.PeakDb;
        VirtualLimiterText = metrics.VirtualGainReductionDb > 0.05f ? $"−{metrics.VirtualGainReductionDb:0.0} dB" : "0,0 dB";
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

    /// <summary>
    /// Micrófono o monitoreo. Los dispositivos del driver de Sonari no se ofrecen: Sonari se grabaría o se oiría a sí
    /// mismo en bucle. Si Windows tiene uno de ellos como predeterminado, la opción dice qué dispositivo se usa en su lugar.
    /// </summary>
    private static void Fill(ObservableCollection<DeviceOption> target, IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        target.Clear();
        AudioEndpointInfo? windowsDefault = endpoints.FirstOrDefault(e => e.IsDefault);
        AudioEndpointInfo? effective = SonariVirtualDevice.EffectiveDefault(endpoints);
        string defaultLabel = windowsDefault is null || !SonariVirtualDevice.IsSonari(windowsDefault)
            ? "Predeterminado de Windows"
            : effective is not null
                ? $"Predeterminado de Windows (es de Sonari, se usa «{effective.Name}»)"
                : "Predeterminado de Windows (es de Sonari y no hay otro dispositivo)";
        target.Add(new DeviceOption(null, defaultLabel));
        foreach (AudioEndpointInfo endpoint in endpoints.Where(e => !SonariVirtualDevice.IsSonari(e)))
        {
            target.Add(new DeviceOption(endpoint.Id, endpoint.Name));
        }
    }

    /// <summary>
    /// Avisos de enrutado: realimentación (el micrófono es la salida del propio cable), monitoreo y micrófono virtual en
    /// el mismo dispositivo, o el cable como salida predeterminada de Windows (todo el audio del PC iría al micrófono).
    /// </summary>
    private void UpdateRoutingChecks()
    {
        RoutingError = null;
        RoutingWarning = null;
        DiscordHint = null;

        AudioEndpointInfo? virtualEndpoint = _outputEndpoints.FirstOrDefault(o => o.Id == SelectedVirtual?.Id);
        if (virtualEndpoint is null)
        {
            return;
        }

        AudioEndpointInfo? captureSide = VirtualCablePairing.FindCaptureSide(virtualEndpoint, _inputEndpoints);
        DiscordHint = captureSide is not null
            ? $"En Discord (o la aplicación que uses), elige como micrófono «{captureSide.Name}»."
            : "En Discord, elige como micrófono la entrada que corresponde a este dispositivo.";

        string? micId = SelectedInput?.Id ?? SonariVirtualDevice.EffectiveDefault(_inputEndpoints)?.Id;
        if (captureSide is not null && micId == captureSide.Id)
        {
            RoutingError = $"Tu micrófono es «{captureSide.Name}», la salida del propio micrófono virtual: se realimentaría. " +
                "Elige tu micrófono real. Mientras tanto, el micrófono virtual queda desactivado.";
        }

        AudioEndpointInfo? defaultOutput = _outputEndpoints.FirstOrDefault(o => o.IsDefault);
        string? monitorId = SelectedMonitor?.Id ?? SonariVirtualDevice.EffectiveDefault(_outputEndpoints)?.Id;
        if (monitorId == virtualEndpoint.Id)
        {
            RoutingError ??= "El monitoreo y el micrófono virtual no pueden ser el mismo dispositivo: los demás oirían tu monitoreo. " +
                "Elige tus auriculares para el monitoreo.";
        }

        if (defaultOutput?.Id == virtualEndpoint.Id)
        {
            RoutingWarning = $"Windows tiene «{virtualEndpoint.Name}» como salida predeterminada: el audio de todas tus aplicaciones " +
                "acabaría en el micrófono virtual. Cambia la salida predeterminada a tus auriculares en la configuración de sonido.";
        }
        else if (!virtualEndpoint.IsLikelyVirtual)
        {
            RoutingWarning = $"«{virtualEndpoint.Name}» no parece un cable virtual: la mezcla sonará por ese dispositivo en vez de llegar a un micrófono.";
        }
    }

    /// <summary>Primero el driver de Sonari, luego otros cables virtuales y al final el resto de salidas.</summary>
    private static void FillVirtual(ObservableCollection<DeviceOption> target, IEnumerable<AudioEndpointInfo> outputs)
    {
        target.Clear();
        target.Add(new DeviceOption(null, "Desactivado"));
        foreach (AudioEndpointInfo endpoint in outputs
            .OrderByDescending(SonariVirtualDevice.IsSonari)
            .ThenByDescending(o => o.IsLikelyVirtual))
        {
            string label = SonariVirtualDevice.IsSonari(endpoint) ? $"{endpoint.Name} · driver de Sonari"
                : endpoint.IsLikelyVirtual ? endpoint.Name
                : $"{endpoint.Name} (no es un cable)";
            target.Add(new DeviceOption(endpoint.Id, label));
        }
    }

    private static DeviceOption Find(ObservableCollection<DeviceOption> options, string? id) =>
        options.FirstOrDefault(o => o.Id == id) ?? options[0];
}
