using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OndaMix.Infrastructure.Settings;
using OndaMix.Infrastructure.Storage;
using OndaMix.Presentation.Services;

namespace OndaMix.Presentation.ViewModels;

/// <summary>
/// Edita una copia de los ajustes de audio; nada se aplica hasta pulsar Guardar. Los dispositivos se eligen aquí
/// a partir de la Fase 2, cuando el motor pueda usarlos.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IShellIntegration _shell;
    private readonly AppPaths _paths;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardChangesCommand))]
    private int _sampleRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardChangesCommand))]
    private int _bufferMilliseconds;

    [ObservableProperty]
    private string? _statusMessage;

    public SettingsViewModel(
        ISettingsService settings,
        IShellIntegration shell,
        AppPaths paths,
        ILogger<SettingsViewModel> logger)
        : base("settings", "Ajustes", "", "Formato del motor, carpetas de datos y diagnóstico.")
    {
        _settings = settings;
        _shell = shell;
        _paths = paths;
        _logger = logger;
        LoadFrom(_settings.Current.Audio);
    }

    public IReadOnlyList<int> SampleRates => AudioSettings.SupportedSampleRates;

    public IReadOnlyList<int> BufferSizes => AudioSettings.SupportedBufferMilliseconds;

    public string ConfigDirectory => _paths.ConfigDirectory;

    public string LogDirectory => _paths.LogDirectory;

    public bool IsDirty =>
        SampleRate != _settings.Current.Audio.SampleRate ||
        BufferMilliseconds != _settings.Current.Audio.BufferMilliseconds;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private async Task SaveAsync()
    {
        AppSettings current = _settings.Current;
        AppSettings updated = current with
        {
            Audio = current.Audio with { SampleRate = SampleRate, BufferMilliseconds = BufferMilliseconds },
        };

        try
        {
            await _settings.SaveAsync(updated);
            StatusMessage = $"Guardado a las {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "No se pudieron guardar los ajustes");
            StatusMessage = $"No se pudo guardar: {ex.Message}";
        }

        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardChangesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void DiscardChanges()
    {
        LoadFrom(_settings.Current.Audio);
        StatusMessage = null;
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        LoadFrom(new AudioSettings());
        StatusMessage = "Valores por defecto cargados. Pulsa Guardar para aplicarlos.";
    }

    [RelayCommand]
    private void OpenConfigFolder() => _shell.OpenFolder(_paths.ConfigDirectory);

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenFolder(_paths.LogDirectory);

    private void LoadFrom(AudioSettings audio)
    {
        SampleRate = audio.SampleRate;
        BufferMilliseconds = audio.BufferMilliseconds;
    }
}
