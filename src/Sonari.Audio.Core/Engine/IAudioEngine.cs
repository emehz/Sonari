using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;

namespace Sonari.Audio.Core.Engine;

public enum EngineState
{
    Stopped,
    Starting,
    Running,

    /// <summary>Un dispositivo falló o desapareció; el motor reintenta solo.</summary>
    Recovering,
}

/// <param name="InputDeviceId"><c>null</c> = micrófono predeterminado de Windows (y lo sigue si cambia).</param>
/// <param name="MonitorDeviceId"><c>null</c> = salida predeterminada de Windows (y la sigue si cambia).</param>
/// <param name="VirtualOutputDeviceId">
/// Salida donde se escribe la mezcla del micrófono virtual (la entrada de un cable virtual, p. ej. «CABLE Input»).
/// <c>null</c> = sin micrófono virtual. Nunca sigue al predeterminado de Windows: eso mandaría la mezcla a tus altavoces.
/// </param>
public sealed record EngineOptions(
    AudioFormat Format,
    int BlockMilliseconds,
    string? InputDeviceId,
    string? MonitorDeviceId,
    string? VirtualOutputDeviceId = null);

/// <summary>Estado legible del motor. Se publica entero en cada cambio (no en cada bloque de audio).</summary>
/// <param name="VirtualDeviceName">Salida del micrófono virtual en uso, o <c>null</c> si no está activo.</param>
/// <param name="VirtualMessage">Por qué el micrófono virtual no está activo (no configurado, no encontrado…), o <c>null</c>.</param>
public sealed record EngineStatus(
    EngineState State,
    string Message,
    string? InputDeviceName = null,
    string? MonitorDeviceName = null,
    double? InputLatencyMilliseconds = null,
    double? OutputLatencyMilliseconds = null,
    string? VirtualDeviceName = null,
    string? VirtualMessage = null)
{
    public static EngineStatus Stopped { get; } = new(EngineState.Stopped, "Motor detenido");
}

/// <summary>Picos lineales (0..1, puede superar 1 antes del limitador) desde la lectura anterior.</summary>
public readonly record struct StereoPeak(float Left, float Right)
{
    public float Max => MathF.Max(Left, Right);
}

/// <summary>
/// Métricas del bus de monitoreo. Es un struct para que leerlas 30 veces por segundo no genere basura.
/// Las de cada fuente están en <see cref="ISourceChannel.ReadMetrics"/>.
/// </summary>
/// <param name="Resyncs">Veces que se descartó audio acumulado (suma de todas las fuentes).</param>
/// <param name="MonitorGainReductionDb">Máxima reducción aplicada por el limitador desde la lectura anterior (0 = no actuó).</param>
/// <param name="ClippedSamples">Muestras que tuvo que recortar la protección final; con el limitador debería quedarse en 0.</param>
public readonly record struct EngineMetrics(
    StereoPeak MonitorOutput,
    float MonitorGainReductionDb,
    long ClippedSamples,
    long Resyncs,
    StereoPeak VirtualOutput = default,
    float VirtualGainReductionDb = 0f);

public interface IChannelControl
{
    float GainDb { get; set; }

    bool IsMuted { get; set; }

    /// <summary>Balance estéreo de −1 (solo izquierda) a 1 (solo derecha). En 0 no altera la señal.</summary>
    float Balance { get; set; }

    bool SendToMonitor { get; set; }

    /// <summary>Si la fuente entra en la mezcla del micrófono virtual (lo que oyen los demás).</summary>
    bool SendToVirtual { get; set; }
}

public interface IBusControl
{
    float GainDb { get; set; }

    bool IsMuted { get; set; }
}

/// <summary>
/// Motor de audio. Los controles de fuentes y buses son seguros desde cualquier hilo y conservan su valor entre
/// arranques. Start/Stop pueden tardar (abren dispositivos): no esperar en el hilo de UI de forma síncrona.
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    EngineStatus Status { get; }

    /// <summary>Formato con el que está funcionando el motor, o <c>null</c> si está detenido.</summary>
    AudioFormat? Format { get; }

    /// <summary>Puede llegar en cualquier hilo.</summary>
    event EventHandler<EngineStatus>? StatusChanged;

    /// <summary>
    /// Se dispara cuando se añade o quita una fuente o cambia el estado de alguna. Puede llegar en cualquier hilo
    /// y en ráfagas: los suscriptores deben releer <see cref="Sources"/>.
    /// </summary>
    event EventHandler? SourcesChanged;

    ISourceChannel Microphone { get; }

    /// <summary>Canal del reproductor de sonidos (siempre existe, no se puede quitar).</summary>
    ISourceChannel SoundChannel { get; }

    /// <summary>Reproductor de sonidos. Sus órdenes solo suenan con el motor en marcha.</summary>
    ISoundPlayer Sounds { get; }

    /// <summary>
    /// Silencio global: corta todo lo que sale de Sonari (monitoreo y, desde la Fase 5, micrófono virtual) con un
    /// fundido corto. Las fuentes siguen capturando, así que al quitarlo el audio vuelve al instante.
    /// </summary>
    bool IsGlobalMuted { get; set; }

    /// <summary>Todas las fuentes, con el micrófono primero. Instantánea: una lista nueva en cada cambio.</summary>
    IReadOnlyList<ISourceChannel> Sources { get; }

    IBusControl Monitor { get; }

    /// <summary>Volumen y silencio de la mezcla que se envía al micrófono virtual, independientes del monitoreo.</summary>
    IBusControl VirtualMicrophone { get; }

    /// <summary>Si es falso, no se pueden añadir fuentes de aplicación ni de sistema.</summary>
    bool SupportsProcessCapture { get; }

    /// <summary>
    /// Añade una aplicación como fuente independiente. Si el motor está en marcha, empieza a capturarla sin tocar las
    /// demás fuentes; si no, se capturará al arrancar. Por defecto no se envía al monitoreo (ya la oyes directamente).
    /// </summary>
    ISourceChannel AddApplicationSource(ApplicationTarget target);

    /// <summary>Añade (o devuelve, si ya existe) la fuente «audio del sistema excepto Sonari».</summary>
    ISourceChannel AddSystemAudioSource();

    /// <summary>Quita una fuente y libera su captura. El micrófono no se puede quitar.</summary>
    void RemoveSource(ISourceChannel source);

    /// <summary>Arranca (o reinicia con nuevas opciones). Si un dispositivo falla, el motor queda en Recovering y reintenta.</summary>
    Task StartAsync(EngineOptions options, CancellationToken cancellationToken = default);

    Task StopAsync();

    /// <summary>Lee y reinicia los picos del monitoreo. Pensado para un único lector (la UI).</summary>
    EngineMetrics ReadMetrics();
}
