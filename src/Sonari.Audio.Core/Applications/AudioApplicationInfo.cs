namespace Sonari.Audio.Core.Applications;

/// <summary>Una aplicación en ejecución, agrupada por su proceso raíz (un navegador con 20 procesos es una sola entrada).</summary>
/// <param name="ProcessId">PID del proceso raíz: es el que se captura, incluyendo a sus hijos.</param>
/// <param name="HasAudioSession">Windows tiene una sesión de audio de esta aplicación (aunque esté en silencio).</param>
/// <param name="IsPlaying">Alguna de sus sesiones está activa ahora mismo.</param>
/// <param name="PeakLevel">Pico lineal actual según el medidor de sesión de Windows (0..1).</param>
public sealed record AudioApplicationInfo(
    int ProcessId,
    string ProcessName,
    string DisplayName,
    string? ExecutablePath,
    string? WindowTitle,
    bool HasAudioSession,
    bool IsPlaying,
    float PeakLevel,
    int ProcessCount);

public interface IAudioApplicationCatalog
{
    /// <summary>Aplicaciones con sesión de audio y, si se pide, también las que tienen ventana pero no audio.
    /// Usa COM y lee procesos: no llamar desde el hilo de UI.</summary>
    IReadOnlyList<AudioApplicationInfo> GetApplications(bool includeWithoutAudio);
}
