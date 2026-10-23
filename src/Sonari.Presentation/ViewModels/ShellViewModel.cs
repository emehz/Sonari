using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Engine;
using Sonari.Infrastructure.Settings;
using Sonari.Infrastructure.Storage;
using Sonari.Presentation.Services;

namespace Sonari.Presentation.ViewModels;

/// <summary>Ventana principal: navegación lateral, página activa y barra de estado.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IShellIntegration _shell;
    private readonly AppPaths _paths;
    private readonly ILogger<ShellViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;

    [ObservableProperty]
    private PageViewModel? _selectedPage;

    [ObservableProperty]
    private string _audioFormatSummary = string.Empty;

    [ObservableProperty]
    private EngineState _engineState;

    [ObservableProperty]
    private string _engineStatusText = string.Empty;

    public ShellViewModel(
        IEnumerable<PageViewModel> pages,
        ISettingsService settings,
        IAudioEngine engine,
        IShellIntegration shell,
        AppPaths paths,
        ILogger<ShellViewModel> logger)
    {
        _settings = settings;
        _shell = shell;
        _paths = paths;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        Pages = pages.ToList();
        UpdateAudioFormatSummary(_settings.Current);
        _settings.Changed += OnSettingsChanged;

        ApplyEngineStatus(engine.Status);
        engine.StatusChanged += (_, status) => PostToUi(() => ApplyEngineStatus(status));

        string? lastPage = _settings.Current.Ui.LastPage;
        SelectedPage = Pages.FirstOrDefault(p => p.Key == lastPage) ?? Pages.FirstOrDefault();
    }

    public IReadOnlyList<PageViewModel> Pages { get; }

    public string AppVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public string WindowsVersion { get; } = $"Windows {Environment.OSVersion.Version}";

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(_paths.LogDirectory);

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        if (value is not null)
        {
            _ = NavigateAsync(value);
        }
    }

    private async Task NavigateAsync(PageViewModel page)
    {
        try
        {
            await page.OnNavigatedToAsync();

            AppSettings current = _settings.Current;
            if (current.Ui.LastPage != page.Key)
            {
                await _settings.SaveAsync(current with { Ui = current.Ui with { LastPage = page.Key } });
            }
        }
        catch (Exception ex)
        {
            // Recordar la última página es una comodidad: si falla, la navegación sigue funcionando.
            _logger.LogWarning(ex, "Error al navegar a {Page}", page.Key);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) => PostToUi(() => UpdateAudioFormatSummary(settings));

    private void ApplyEngineStatus(EngineStatus status)
    {
        EngineState = status.State;
        EngineStatusText = $"Motor: {status.Message}";
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

    private void UpdateAudioFormatSummary(AppSettings settings) =>
        AudioFormatSummary = $"{settings.Audio.SampleRate / 1000.0:0.#} kHz · bloque de {settings.Audio.BufferMilliseconds} ms";
}
