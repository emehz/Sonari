using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;
using Sonari.Infrastructure.Settings;
using Sonari.Infrastructure.Soundboard;
using Sonari.Presentation.Services;

namespace Sonari.Presentation.ViewModels;

/// <summary>
/// Panel de sonidos: bancos, sonidos y reproducción. Los sonidos se decodifican en segundo plano al abrir un banco para
/// que pulsar «reproducir» sea instantáneo. Los atajos de teclado globales llegarán en la Fase 7.
/// </summary>
public sealed partial class SoundboardViewModel : PageViewModel
{
    public const string AllCategories = "Todas las categorías";

    private static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly IAudioEngine _engine;
    private readonly ISoundClipProvider _clips;
    private readonly ISoundboardStore _store;
    private readonly ISettingsService _settings;
    private readonly IShellIntegration _shell;
    private readonly ILogger<SoundboardViewModel> _logger;
    private readonly DispatcherTimer _stateTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly SynchronizationContext? _uiContext;
    private bool _loading;

    [ObservableProperty]
    private SoundBankViewModel? _selectedBank;

    [ObservableProperty]
    private string _categoryFilter = AllCategories;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _engineRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterGainText))]
    private double _masterGainDb;

    [ObservableProperty]
    private int _activeVoices;

    public SoundboardViewModel(
        IAudioEngine engine,
        ISoundClipProvider clips,
        ISoundboardStore store,
        ISettingsService settings,
        IShellIntegration shell,
        ILogger<SoundboardViewModel> logger)
        : base("soundboard", "Sonidos", "\uE8D6",
            "Bancos de sonidos que suenan por el canal «Sonidos» del mezclador.")
    {
        _engine = engine;
        _clips = clips;
        _store = store;
        _settings = settings;
        _shell = shell;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;
        _masterGainDb = engine.SoundChannel.GainDb;

        _stateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = StateInterval };
        _stateTimer.Tick += (_, _) => RefreshPlaybackStates();
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveAsync();
        };

        _engine.StatusChanged += (_, status) => PostToUi(() => EngineRunning = status.State == EngineState.Running);
        EngineRunning = _engine.Status.State == EngineState.Running;

        LoadLibrary();
    }

    public ObservableCollection<SoundBankViewModel> Banks { get; } = [];

    public ObservableCollection<string> Categories { get; } = [AllCategories];

    /// <summary>Sonidos del banco seleccionado que pasan el filtro de categoría.</summary>
    public ObservableCollection<SoundItemViewModel> VisibleSounds { get; } = [];

    public string MasterGainText => MasterGainDb <= -60 ? "−∞ dB" : $"{MasterGainDb:+0.0;−0.0;0.0} dB";

    public string SupportedFormatsText => string.Join(", ", _clips.SupportedExtensions.Select(e => e.TrimStart('.').ToUpperInvariant()));

    public override Task OnNavigatedToAsync()
    {
        _stateTimer.Start();
        PreloadSelectedBank();
        return Task.CompletedTask;
    }

    public override void OnNavigatedFrom() => _stateTimer.Stop();

    partial void OnSelectedBankChanged(SoundBankViewModel? value)
    {
        CategoryFilter = AllCategories;
        RebuildCategories();
        RebuildVisible();
        PreloadSelectedBank();
    }

    partial void OnCategoryFilterChanged(string value) => RebuildVisible();

    partial void OnMasterGainDbChanged(double value) => _engine.SoundChannel.GainDb = (float)value;

    [RelayCommand]
    private void StopAll()
    {
        _engine.Sounds.StopAll();
        Message = null;
    }

    [RelayCommand]
    private async Task PlayAsync(SoundItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!EngineRunning)
        {
            Message = "Inicia el motor en el Mezclador para oír los sonidos.";
            return;
        }

        item.RefreshFileExists();
        if (item.IsMissing)
        {
            item.Error = "No se encuentra el archivo. ¿Se movió o se borró?";
            return;
        }

        AudioFormat format = _engine.Format ?? CurrentFormat();
        item.IsLoading = true;
        try
        {
            SoundClip clip = await _clips.GetAsync(item.FilePath, format);
            item.Duration = clip.Duration;
            item.Error = null;
            _engine.Sounds.Play(item.Id, clip, (float)item.GainDb, item.Loop);
        }
        catch (SoundDecodeException ex)
        {
            item.Error = ex.Message;
        }
        finally
        {
            item.IsLoading = false;
        }
    }

    [RelayCommand]
    private void TogglePause(SoundItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsPlaying)
        {
            _engine.Sounds.Pause(item.Id);
        }
        else if (item.IsPaused)
        {
            _engine.Sounds.Resume(item.Id);
        }
    }

    [RelayCommand]
    private void Stop(SoundItemViewModel? item)
    {
        if (item is not null)
        {
            _engine.Sounds.Stop(item.Id);
        }
    }

    [RelayCommand]
    private void ImportSounds()
    {
        if (SelectedBank is null)
        {
            return;
        }

        IReadOnlyList<string> files = _shell.PickAudioFiles(_clips.SupportedExtensions);
        if (files.Count == 0)
        {
            return;
        }

        foreach (string file in files)
        {
            var entry = new SoundEntry(Guid.NewGuid(), Path.GetFileNameWithoutExtension(file), file,
                CategoryFilter == AllCategories ? null : CategoryFilter, 0f, false);
            SelectedBank.Sounds.Add(new SoundItemViewModel(entry, OnSoundEdited));
        }

        Message = files.Count == 1 ? "1 sonido importado." : $"{files.Count} sonidos importados.";
        RebuildCategories();
        RebuildVisible();
        PreloadSelectedBank();
        ScheduleSave();
    }

    [RelayCommand]
    private void RemoveSound(SoundItemViewModel? item)
    {
        if (item is null || SelectedBank is null)
        {
            return;
        }

        _engine.Sounds.Stop(item.Id);
        SelectedBank.Sounds.Remove(item);
        _clips.Forget(item.FilePath);
        RebuildCategories();
        RebuildVisible();
        ScheduleSave();
    }

    [RelayCommand]
    private void MoveUp(SoundItemViewModel? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(SoundItemViewModel? item) => Move(item, +1);

    [RelayCommand]
    private void AddBank()
    {
        var bank = new SoundBankViewModel(
            new SoundBank(Guid.NewGuid(), $"Banco {Banks.Count + 1}", []), ScheduleSave, OnSoundEdited);
        Banks.Add(bank);
        SelectedBank = bank;
        ScheduleSave();
    }

    [RelayCommand]
    private void RemoveBank()
    {
        if (SelectedBank is null || Banks.Count <= 1)
        {
            Message = "Tiene que quedar al menos un banco.";
            return;
        }

        SoundBankViewModel bank = SelectedBank;
        if (bank.Sounds.Count > 0 &&
            !_shell.Confirm($"¿Quitar el banco «{bank.Name}» y sus {bank.Sounds.Count} sonidos del panel? Los archivos no se borran.",
                "Quitar banco"))
        {
            return;
        }

        foreach (SoundItemViewModel sound in bank.Sounds)
        {
            _engine.Sounds.Stop(sound.Id);
        }

        int index = Banks.IndexOf(bank);
        Banks.Remove(bank);
        SelectedBank = Banks[Math.Min(index, Banks.Count - 1)];
        ScheduleSave();
    }

    private void Move(SoundItemViewModel? item, int delta)
    {
        if (item is null || SelectedBank is null)
        {
            return;
        }

        // Se mueve dentro del banco completo, saltando a la siguiente posición visible si hay filtro.
        ObservableCollection<SoundItemViewModel> sounds = SelectedBank.Sounds;
        int visibleIndex = VisibleSounds.IndexOf(item);
        int targetVisible = visibleIndex + delta;
        if (visibleIndex < 0 || targetVisible < 0 || targetVisible >= VisibleSounds.Count)
        {
            return;
        }

        sounds.Move(sounds.IndexOf(item), sounds.IndexOf(VisibleSounds[targetVisible]));
        RebuildVisible();
        ScheduleSave();
    }

    private void OnSoundEdited(SoundItemViewModel item)
    {
        if (_loading)
        {
            return;
        }

        // Volumen y bucle se aplican también a la reproducción en curso.
        _engine.Sounds.SetGain(item.Id, (float)item.GainDb);
        _engine.Sounds.SetLoop(item.Id, item.Loop);
        RebuildCategories();
        ScheduleSave();
    }

    private void LoadLibrary()
    {
        _loading = true;
        try
        {
            SoundboardLibrary library = _store.Load();
            foreach (SoundBank bank in library.Banks)
            {
                Banks.Add(new SoundBankViewModel(bank, ScheduleSave, OnSoundEdited));
            }

            SelectedBank = Banks.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
    }

    private void PreloadSelectedBank()
    {
        if (SelectedBank is null)
        {
            return;
        }

        AudioFormat format = _engine.Format ?? CurrentFormat();
        foreach (SoundItemViewModel item in SelectedBank.Sounds.Where(s => !s.IsMissing && s.Duration is null))
        {
            _ = PreloadAsync(item, format);
        }
    }

    private async Task PreloadAsync(SoundItemViewModel item, AudioFormat format)
    {
        try
        {
            SoundClip clip = await _clips.GetAsync(item.FilePath, format);
            item.Duration = clip.Duration;
            item.ApplyState(SoundPlaybackState.Stopped);
        }
        catch (SoundDecodeException ex)
        {
            item.Error = ex.Message;
        }
    }

    private void RefreshPlaybackStates()
    {
        if (SelectedBank is null)
        {
            return;
        }

        foreach (SoundItemViewModel item in SelectedBank.Sounds)
        {
            item.ApplyState(_engine.Sounds.GetState(item.Id));
        }

        ActiveVoices = _engine.Sounds.ActiveVoices;
    }

    private void RebuildCategories()
    {
        string current = CategoryFilter;
        var categories = (SelectedBank?.Sounds ?? [])
            .Select(s => s.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Categories.Clear();
        Categories.Add(AllCategories);
        foreach (string category in categories)
        {
            Categories.Add(category);
        }

        if (!Categories.Contains(current))
        {
            CategoryFilter = AllCategories;
        }
    }

    private void RebuildVisible()
    {
        VisibleSounds.Clear();
        if (SelectedBank is null)
        {
            return;
        }

        foreach (SoundItemViewModel sound in SelectedBank.Sounds)
        {
            if (CategoryFilter == AllCategories ||
                string.Equals(sound.Category?.Trim(), CategoryFilter, StringComparison.CurrentCultureIgnoreCase))
            {
                VisibleSounds.Add(sound);
            }
        }
    }

    private void ScheduleSave()
    {
        if (_loading)
        {
            return;
        }

        // Varias ediciones seguidas (arrastrar un slider) se agrupan en un solo guardado.
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(new SoundboardLibrary { Banks = Banks.Select(b => b.ToBank()).ToList() });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "No se pudo guardar el panel de sonidos");
            Message = $"No se pudo guardar el panel de sonidos: {ex.Message}";
        }
    }

    private AudioFormat CurrentFormat() => new(_settings.Current.Audio.SampleRate, 2);

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
}
