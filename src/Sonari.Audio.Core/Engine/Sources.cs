namespace Sonari.Audio.Core.Engine;

public enum SourceKind
{
    Microphone,

    /// <summary>Audio de una aplicación concreta (y sus procesos hijos), mediante Application Loopback.</summary>
    Application,

    /// <summary>Todo lo que suena en Windows excepto el propio Sonari (Application Loopback en modo exclusión).</summary>
    SystemAudio,

    /// <summary>Reproductor de sonidos: todos los sonidos del panel salen por este canal.</summary>
    SoundPlayer,
}

public enum SourceStatus
{
    /// <summary>El motor está detenido.</summary>
    Stopped,

    /// <summary>Esperando a que la aplicación se abra (o a reintentar tras un fallo).</summary>
    Waiting,

    Active,

    Faulted,
}

/// <summary>
/// Aplicación a capturar. Se identifica por la ruta del ejecutable y no por el PID, para poder volver a engancharla
/// cuando se cierra y se abre de nuevo.
/// </summary>
/// <param name="PreferredProcessId">Instancia concreta elegida por el usuario; si deja de existir se usa cualquier otra.</param>
public sealed record ApplicationTarget(string ExecutablePath, string DisplayName, int? PreferredProcessId = null);

/// <summary>Métricas de un canal. Los picos son desde la lectura anterior; los contadores, acumulados desde el arranque.</summary>
/// <param name="Underruns">Vaciados mientras la fuente estaba enviando audio (indican un problema).</param>
/// <param name="IdleGaps">Vaciados porque la fuente dejó de enviar (una aplicación que se calla; es normal).</param>
public readonly record struct ChannelMetrics(
    StereoPeak Input,
    StereoPeak PostFader,
    double BufferMilliseconds,
    double DriftPpm,
    long Underruns,
    long IdleGaps,
    long OverrunSamples,
    long Discontinuities);

/// <summary>Una fuente del mezclador. Los controles heredados de <see cref="IChannelControl"/> se pueden usar desde cualquier hilo.</summary>
public interface ISourceChannel : IChannelControl
{
    Guid Id { get; }

    SourceKind Kind { get; }

    string DisplayName { get; }

    /// <summary>Solo para <see cref="SourceKind.Application"/>.</summary>
    string? ExecutablePath { get; }

    SourceStatus Status { get; }

    string StatusMessage { get; }

    /// <summary>Proceso capturado ahora mismo, si lo hay.</summary>
    int? ProcessId { get; }

    /// <summary>Lee y reinicia los picos. Pensado para un único lector (la UI).</summary>
    ChannelMetrics ReadMetrics();
}
