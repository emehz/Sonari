using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Sonari.Audio.Core.Playback;
using Sonari.Infrastructure.Soundboard;

namespace Sonari.Presentation.ViewModels;

/// <summary>Un sonido del panel. Los cambios de nombre, categoría, volumen o bucle se notifican para guardar la biblioteca.</summary>
public sealed partial class SoundItemViewModel : ObservableObject
{
    private readonly Action<SoundItemViewModel> _onEdited;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string? _category;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    private double _gainDb;

    [ObservableProperty]
    private bool _loop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaying), nameof(IsPaused), nameof(IsActive), nameof(PlayGlyph))]
    private SoundPlaybackStatus _status;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _timeText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isMissing;

    public SoundItemViewModel(SoundEntry entry, Action<SoundItemViewModel> onEdited)
    {
        Id = entry.Id;
        FilePath = entry.FilePath;
        _name = entry.Name;
        _category = entry.Category;
        _gainDb = entry.GainDb;
        _loop = entry.Loop;
        _onEdited = onEdited;
        RefreshFileExists();
    }

    public Guid Id { get; }

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public TimeSpan? Duration { get; set; }

    public bool IsPlaying => Status == SoundPlaybackStatus.Playing;

    public bool IsPaused => Status == SoundPlaybackStatus.Paused;

    public bool IsActive => Status != SoundPlaybackStatus.Stopped;

    /// <summary>Reproducir (▶) o reiniciar (⟲) si ya está sonando.</summary>
    public string PlayGlyph => IsActive ? "\uE72C" : "\uE768";

    public string GainText => GainDb <= SoundEntry.MinGainDb ? "−∞ dB" : $"{GainDb:+0.0;−0.0;0.0} dB";

    public SoundEntry ToEntry() => new(Id, Name, FilePath, Category, (float)GainDb, Loop);

    public void RefreshFileExists() => IsMissing = !File.Exists(FilePath);

    public void ApplyState(SoundPlaybackState state)
    {
        Status = state.Status;
        Progress = state.Progress;
        TimeSpan total = state.Status == SoundPlaybackStatus.Stopped ? Duration ?? TimeSpan.Zero : state.Duration;
        TimeText = state.Status == SoundPlaybackStatus.Stopped
            ? FormatTime(total)
            : $"{FormatTime(state.Position)} / {FormatTime(total)}";
    }

    partial void OnNameChanged(string value) => _onEdited(this);

    partial void OnCategoryChanged(string? value) => _onEdited(this);

    partial void OnGainDbChanged(double value) => _onEdited(this);

    partial void OnLoopChanged(bool value) => _onEdited(this);

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}
