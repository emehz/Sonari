using Microsoft.Extensions.Logging;
using Sonari.Infrastructure.Storage;

namespace Sonari.Infrastructure.Soundboard;

/// <summary>
/// Biblioteca del panel de sonidos: bancos (pestañas) con sus sonidos. Los sonidos se guardan por ruta de archivo; no se
/// copian, así que si se mueve o borra el archivo, el sonido aparece como «no encontrado».
/// </summary>
public sealed record SoundboardLibrary
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<SoundBank> Banks { get; init; } = [SoundBank.CreateDefault()];

    public SoundboardLibrary Normalize()
    {
        List<SoundBank> banks = (Banks ?? [])
            .Where(b => b is not null)
            .Select(b => b.Normalize())
            .ToList();

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Banks = banks.Count > 0 ? banks : [SoundBank.CreateDefault()],
        };
    }
}

public sealed record SoundBank(Guid Id, string Name, IReadOnlyList<SoundEntry> Sounds)
{
    public static SoundBank CreateDefault() => new(Guid.NewGuid(), "General", []);

    public SoundBank Normalize() => this with
    {
        Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
        Name = string.IsNullOrWhiteSpace(Name) ? "Banco" : Name.Trim(),
        Sounds = (Sounds ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.FilePath)).Select(s => s.Normalize()).ToList(),
    };
}

/// <param name="Category">Etiqueta libre para filtrar (p. ej. «Risas», «Música»). Opcional.</param>
/// <param name="GainDb">Volumen propio del sonido, de −60 a +12 dB.</param>
public sealed record SoundEntry(Guid Id, string Name, string FilePath, string? Category, float GainDb, bool Loop)
{
    public const float MinGainDb = -60f;
    public const float MaxGainDb = 12f;

    public SoundEntry Normalize() => this with
    {
        Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
        Name = string.IsNullOrWhiteSpace(Name) ? Path.GetFileNameWithoutExtension(FilePath) : Name.Trim(),
        Category = string.IsNullOrWhiteSpace(Category) ? null : Category.Trim(),
        GainDb = float.IsNaN(GainDb) ? 0f : Math.Clamp(GainDb, MinGainDb, MaxGainDb),
    };
}

public interface ISoundboardStore
{
    SoundboardLibrary Load();

    Task SaveAsync(SoundboardLibrary library, CancellationToken cancellationToken = default);
}

public sealed class SoundboardStore(AppPaths paths, JsonFileStore store, ILogger<SoundboardStore> logger) : ISoundboardStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SoundboardLibrary Load()
    {
        SoundboardLibrary? loaded = store.TryLoad<SoundboardLibrary>(paths.SoundboardFile);
        if (loaded is null)
        {
            logger.LogInformation("Panel de sonidos vacío (no había biblioteca en {Path})", paths.SoundboardFile);
        }

        return (loaded ?? new SoundboardLibrary()).Normalize();
    }

    public async Task SaveAsync(SoundboardLibrary library, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await store.SaveAsync(paths.SoundboardFile, library.Normalize(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
