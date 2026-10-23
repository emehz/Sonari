using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sonari.Audio.Core.Applications;
using Sonari.Audio.Core.Formats;
using Sonari.Presentation.Interop;

namespace Sonari.Presentation.ViewModels;

/// <summary>Una aplicación en el selector. Se actualiza en su sitio cada segundo para que la lista no parpadee.</summary>
public sealed partial class ApplicationItemViewModel : ObservableObject
{
    // Aplicaciones de llamadas: capturarlas hacia el micrófono virtual haría que los demás se oyeran a sí mismos.
    private static readonly string[] CallApplications =
        ["discord", "teams", "ms-teams", "zoom", "skype", "slack", "whatsapp", "telegram", "signal"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private int _processId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private int _processCount;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string? _windowTitle;

    [ObservableProperty]
    private bool _hasAudioSession;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _levelDb = Decibels.Floor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(AddButtonText))]
    private bool _isAdded;

    public ApplicationItemViewModel(AudioApplicationInfo info)
    {
        ProcessName = info.ProcessName;
        ExecutablePath = info.ExecutablePath;
        Icon = ShellIcons.ForExecutable(info.ExecutablePath);
        string name = Path.GetFileNameWithoutExtension(info.ProcessName).ToLowerInvariant();
        IsCallApplication = CallApplications.Any(name.Contains);
        _displayName = info.DisplayName;
        Update(info);
    }

    public string ProcessName { get; }

    /// <summary><c>null</c> si Windows no deja leer la ruta (proceso protegido): no se puede añadir.</summary>
    public string? ExecutablePath { get; }

    public ImageSource? Icon { get; }

    public bool HasIcon => Icon is not null;

    public bool IsCallApplication { get; }

    public string Subtitle => ProcessCount > 1
        ? $"{ProcessName} · PID {ProcessId} · {ProcessCount} procesos"
        : $"{ProcessName} · PID {ProcessId}";

    public bool CanAdd => !IsAdded && ExecutablePath is not null;

    public string AddButtonText => IsAdded ? "Añadida" : "Añadir";

    public string? AddDisabledReason => ExecutablePath is null
        ? "Windows no permite leer la ruta de este proceso (está protegido o elevado)."
        : null;

    public void Update(AudioApplicationInfo info)
    {
        ProcessId = info.ProcessId;
        ProcessCount = info.ProcessCount;
        DisplayName = info.DisplayName;
        WindowTitle = info.WindowTitle;
        HasAudioSession = info.HasAudioSession;
        IsPlaying = info.IsPlaying;
        LevelDb = Decibels.FromLinear(info.PeakLevel);
    }
}
