using Microsoft.Extensions.Logging;
using OndaMix.Infrastructure.Storage;

namespace OndaMix.Infrastructure.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Carga los ajustes al construirse (archivo pequeño y local, en el arranque) y los guarda de uno en uno para que
/// dos guardados seguidos no compitan por el archivo temporal.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly AppPaths _paths;
    private readonly JsonFileStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private AppSettings _current;

    public SettingsService(AppPaths paths, JsonFileStore store, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _store = store;
        _logger = logger;

        AppSettings? loaded = _store.TryLoad<AppSettings>(_paths.SettingsFile);
        _current = (loaded ?? new AppSettings()).Normalize();
        if (loaded is null)
        {
            _logger.LogInformation("Ajustes por defecto: no había un archivo válido en {Path}", _paths.SettingsFile);
        }
        else
        {
            _logger.LogInformation("Ajustes cargados de {Path}", _paths.SettingsFile);
        }
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public event EventHandler<AppSettings>? Changed;

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings normalized = settings.Normalize();

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(_paths.SettingsFile, normalized, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _saveGate.Release();
        }

        _logger.LogInformation("Ajustes guardados");
        Changed?.Invoke(this, normalized);
    }
}
