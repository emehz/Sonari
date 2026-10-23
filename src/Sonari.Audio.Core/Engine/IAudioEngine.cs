using Sonari.Audio.Core.Formats;

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
public sealed record EngineOptions(
    AudioFormat Format,
    int BlockMilliseconds,
    string? InputDeviceId,
    string? MonitorDeviceId);

/// <summary>Estado legible del motor. Se publica entero en cada cambio (no en cada bloque de audio).</summary>
public sealed record EngineStatus(
    EngineState State,
    string Message,
    string? InputDeviceName = null,
    string? MonitorDeviceName = null,
    double? InputLatencyMilliseconds = null,
    double? OutputLatencyMilliseconds = null)
{
    public static EngineStatus Stopped { get; } = new(EngineState.Stopped, "Motor detenido");
}

/// <summary>Picos lineales (0..1, puede superar 1 antes del limitador) desde la lectura anterior.</summary>
public readonly record struct StereoPeak(float Left, float Right)
{
    public float Max => MathF.Max(Left, Right);
}

/// <summary>
/// Métricas en tiempo real. Es un struct para que leerlas 30 veces por segundo no genere basura.
/// Los contadores son acumulados desde el arranque; los picos, desde la lectura anterior.
/// </summary>
public readonly record struct EngineMetrics(
    StereoPeak MicrophoneInput,
    StereoPeak MicrophonePostFader,
    StereoPeak MonitorOutput,
    double MonitorBufferMilliseconds,
    double MonitorDriftPpm,
    long Underruns,
    long OverrunSamples,
    long Resyncs,
    long CaptureDiscontinuities,
    long ClippedSamples);

public interface IChannelControl
{
    float GainDb { get; set; }

    bool IsMuted { get; set; }

    bool SendToMonitor { get; set; }
}

public interface IBusControl
{
    float GainDb { get; set; }

    bool IsMuted { get; set; }
}

/// <summary>
/// Motor de audio. Los controles (<see cref="Microphone"/>, <see cref="Monitor"/>) son seguros desde cualquier hilo y
/// conservan su valor entre arranques. Start/Stop pueden tardar (abren dispositivos): no esperar en el hilo de UI de forma síncrona.
/// </summary>
public interface IAudioEngine : IAsyncDisposable
{
    EngineStatus Status { get; }

    /// <summary>Puede llegar en cualquier hilo.</summary>
    event EventHandler<EngineStatus>? StatusChanged;

    IChannelControl Microphone { get; }

    IBusControl Monitor { get; }

    /// <summary>Arranca (o reinicia con nuevas opciones). Si un dispositivo falla, el motor queda en Recovering y reintenta.</summary>
    Task StartAsync(EngineOptions options, CancellationToken cancellationToken = default);

    Task StopAsync();

    /// <summary>Lee y reinicia los picos. Pensado para un único lector (la UI).</summary>
    EngineMetrics ReadMetrics();
}
