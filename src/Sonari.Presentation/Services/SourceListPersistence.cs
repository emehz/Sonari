using System.IO;
using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Engine;
using Sonari.Infrastructure.Settings;

namespace Sonari.Presentation.Services;

/// <summary>
/// Mantiene sincronizada la lista de fuentes del motor con los ajustes: la restaura al arrancar y la guarda cuando se
/// añade o quita una fuente. Solo guarda qué fuentes hay; volúmenes y envíos llegarán con las escenas (Fase 7).
/// </summary>
public sealed class SourceListPersistence(IAudioEngine engine, ISettingsService settings, ILogger<SourceListPersistence> logger)
{
    private int _started;

    public void Restore()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        foreach (SavedSource saved in settings.Current.Audio.Sources)
        {
            try
            {
                if (saved.Kind == SavedSource.SystemAudioKind)
                {
                    engine.AddSystemAudioSource();
                }
                else if (saved.Kind == SavedSource.ApplicationKind)
                {
                    engine.AddApplicationSource(new ApplicationTarget(saved.ExecutablePath!, saved.DisplayName));
                }
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "No se pudo restaurar la fuente «{Source}»", saved.DisplayName);
            }
        }

        engine.SourcesChanged += OnSourcesChanged;
    }

    private void OnSourcesChanged(object? sender, EventArgs e)
    {
        List<SavedSource> current = engine.Sources
            .Where(s => s.Kind != SourceKind.Microphone)
            .Select(s => s.Kind == SourceKind.SystemAudio
                ? new SavedSource(SavedSource.SystemAudioKind, null, s.DisplayName)
                : new SavedSource(SavedSource.ApplicationKind, s.ExecutablePath, s.DisplayName))
            .ToList();

        // El evento también salta en cada cambio de estado; solo se escribe si cambió la lista.
        AppSettings stored = settings.Current;
        if (current.SequenceEqual(stored.Audio.Sources))
        {
            return;
        }

        _ = SaveAsync(stored with { Audio = stored.Audio with { Sources = current } });
    }

    private async Task SaveAsync(AppSettings updated)
    {
        try
        {
            await settings.SaveAsync(updated).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "No se pudo guardar la lista de fuentes");
        }
    }
}
