using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Applications;
using Sonari.Audio.Core.Engine;

namespace Sonari.Presentation.ViewModels;

/// <summary>
/// Selector de fuentes: qué hay en el mezclador, audio del sistema y lista de aplicaciones con audio.
/// La lista se refresca cada segundo solo mientras la página está visible.
/// </summary>
public sealed partial class SourcesViewModel : PageViewModel
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly IAudioEngine _engine;
    private readonly IAudioApplicationCatalog _catalog;
    private readonly ILogger<SourcesViewModel> _logger;
    private readonly SynchronizationContext? _uiContext;
    private readonly DispatcherTimer _refreshTimer;
    private bool _refreshing;

    [ObservableProperty]
    private bool _showAllApplications;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddSystemAudio))]
    private bool _hasSystemAudio;

    [ObservableProperty]
    private bool _hasDuplicationRisk;

    public SourcesViewModel(IAudioEngine engine, IAudioApplicationCatalog catalog, ILogger<SourcesViewModel> logger)
        : base("sources", "Fuentes", "",
            "Elige qué aplicaciones entran en el mezclador. Cada una es un canal independiente.")
    {
        _engine = engine;
        _catalog = catalog;
        _logger = logger;
        _uiContext = SynchronizationContext.Current;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refreshTimer.Tick += async (_, _) => await RefreshApplicationsAsync();

        _engine.SourcesChanged += (_, _) => PostToUi(SyncAddedSources);
        SyncAddedSources();
    }

    public ObservableCollection<ChannelViewModel> AddedSources { get; } = [];

    public ObservableCollection<ApplicationItemViewModel> Applications { get; } = [];

    public bool SupportsCapture => _engine.SupportsProcessCapture;

    public bool CanAddSystemAudio => SupportsCapture && !HasSystemAudio;

    public override async Task OnNavigatedToAsync()
    {
        await RefreshApplicationsAsync();
        _refreshTimer.Start();
    }

    public override void OnNavigatedFrom() => _refreshTimer.Stop();

    partial void OnShowAllApplicationsChanged(bool value) => _ = RefreshApplicationsAsync();

    [RelayCommand]
    private async Task RefreshAsync() => await RefreshApplicationsAsync();

    [RelayCommand]
    private void AddApplication(ApplicationItemViewModel? item)
    {
        if (item?.ExecutablePath is null || item.IsAdded)
        {
            return;
        }

        try
        {
            _engine.AddApplicationSource(new ApplicationTarget(item.ExecutablePath, item.DisplayName, item.ProcessId));
        }
        catch (NotSupportedException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void AddSystemAudio()
    {
        try
        {
            _engine.AddSystemAudioSource();
        }
        catch (NotSupportedException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveSource(ChannelViewModel? channel)
    {
        if (channel is { CanRemove: true })
        {
            _engine.RemoveSource(channel.Source);
        }
    }

    private async Task RefreshApplicationsAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        IsLoading = Applications.Count == 0;
        try
        {
            bool includeAll = ShowAllApplications;
            IReadOnlyList<AudioApplicationInfo> apps = await Task.Run(() => _catalog.GetApplications(includeAll));
            Merge(apps);
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al leer las aplicaciones con audio");
            ErrorMessage = $"No se pudo leer la lista de aplicaciones: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _refreshing = false;
        }
    }

    /// <summary>Actualiza la lista en su sitio (por PID) en vez de reconstruirla, para no perder el scroll ni parpadear.</summary>
    private void Merge(IReadOnlyList<AudioApplicationInfo> apps)
    {
        var byPid = apps.ToDictionary(a => a.ProcessId);
        for (int i = Applications.Count - 1; i >= 0; i--)
        {
            if (!byPid.ContainsKey(Applications[i].ProcessId))
            {
                Applications.RemoveAt(i);
            }
        }

        for (int index = 0; index < apps.Count; index++)
        {
            AudioApplicationInfo info = apps[index];
            int existing = IndexOf(info.ProcessId);
            if (existing < 0)
            {
                Applications.Insert(index, new ApplicationItemViewModel(info));
            }
            else
            {
                Applications[existing].Update(info);
                if (existing != index)
                {
                    Applications.Move(existing, index);
                }
            }
        }

        MarkAdded();
    }

    private int IndexOf(int processId)
    {
        for (int i = 0; i < Applications.Count; i++)
        {
            if (Applications[i].ProcessId == processId)
            {
                return i;
            }
        }

        return -1;
    }

    private void SyncAddedSources()
    {
        IReadOnlyList<ISourceChannel> sources = _engine.Sources;
        for (int i = AddedSources.Count - 1; i >= 0; i--)
        {
            if (!sources.Any(s => s.Id == AddedSources[i].Id))
            {
                AddedSources.RemoveAt(i);
            }
        }

        foreach (ISourceChannel source in sources)
        {
            ChannelViewModel? existing = AddedSources.FirstOrDefault(c => c.Id == source.Id);
            if (existing is null)
            {
                AddedSources.Add(new ChannelViewModel(source));
            }
            else
            {
                existing.RefreshStatus();
            }
        }

        HasSystemAudio = sources.Any(s => s.Kind == SourceKind.SystemAudio);
        HasDuplicationRisk = HasSystemAudio && sources.Any(s => s.Kind == SourceKind.Application);
        MarkAdded();
    }

    private void MarkAdded()
    {
        var addedPaths = _engine.Sources
            .Where(s => s.ExecutablePath is not null)
            .Select(s => s.ExecutablePath!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (ApplicationItemViewModel item in Applications)
        {
            item.IsAdded = item.ExecutablePath is not null && addedPaths.Contains(item.ExecutablePath);
        }
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
}
