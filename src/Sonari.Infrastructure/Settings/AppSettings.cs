using System.Text.Json.Serialization;

namespace Sonari.Infrastructure.Settings;

/// <summary>
/// Preferencias globales. Es un objeto inmutable: para cambiar algo se crea una copia con <c>with</c> y se guarda.
/// Las escenas y perfiles irán en archivos propios (Fase 7); aquí solo hay lo que no depende de la escena.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public AudioSettings Audio { get; init; } = new();

    public UiSettings Ui { get; init; } = new();

    /// <summary>Corrige valores fuera de rango (p. ej. tras editar el JSON a mano) en vez de fallar al arrancar.</summary>
    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        Audio = (Audio ?? new AudioSettings()).Normalize(),
        Ui = Ui ?? new UiSettings(),
    };
}

public sealed record AudioSettings
{
    public static readonly IReadOnlyList<int> SupportedSampleRates = [44_100, 48_000, 96_000];

    public static readonly IReadOnlyList<int> SupportedBufferMilliseconds = [5, 10, 20, 30, 50];

    public const int DefaultSampleRate = 48_000;

    public const int DefaultBufferMilliseconds = 10;

    public int SampleRate { get; init; } = DefaultSampleRate;

    /// <summary>Tamaño de bloque de procesamiento objetivo. El dispositivo puede imponer otro.</summary>
    public int BufferMilliseconds { get; init; } = DefaultBufferMilliseconds;

    /// <summary>Id de endpoint de MMDevice. <c>null</c> significa «el predeterminado de Windows».</summary>
    public string? InputDeviceId { get; init; }

    public string? MonitorDeviceId { get; init; }

    public string? VirtualOutputDeviceId { get; init; }

    /// <summary>Fuentes de aplicación y de sistema que el usuario añadió al mezclador (el micrófono siempre está).</summary>
    public IReadOnlyList<SavedSource> Sources { get; init; } = [];

    public AudioSettings Normalize() => this with
    {
        SampleRate = SupportedSampleRates.Contains(SampleRate) ? SampleRate : DefaultSampleRate,
        BufferMilliseconds = SupportedBufferMilliseconds.Contains(BufferMilliseconds)
            ? BufferMilliseconds
            : DefaultBufferMilliseconds,
        Sources = (Sources ?? []).Where(s => s is not null && s.IsValid).ToList(),
    };
}

/// <summary>Una fuente guardada. Se identifica por el ejecutable, nunca por el PID (cambia en cada arranque).</summary>
/// <param name="Kind"><see cref="ApplicationKind"/> o <see cref="SystemAudioKind"/>.</param>
public sealed record SavedSource(string Kind, string? ExecutablePath, string DisplayName)
{
    public const string ApplicationKind = "Application";
    public const string SystemAudioKind = "SystemAudio";

    [JsonIgnore]
    public bool IsValid => Kind switch
    {
        ApplicationKind => !string.IsNullOrWhiteSpace(ExecutablePath),
        SystemAudioKind => true,
        _ => false,
    };
}

public sealed record UiSettings
{
    /// <summary>Clave de la última sección abierta, para volver a ella al arrancar.</summary>
    public string? LastPage { get; init; }
}
