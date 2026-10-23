using OndaMix.Audio.Core.Formats;

namespace OndaMix.Audio.Core.Endpoints;

/// <summary>
/// Recibe audio capturado en el hilo de captura. <paramref name="interleaved"/> solo es válido durante la llamada.
/// No debe bloquear, asignar memoria ni lanzar excepciones.
/// </summary>
/// <param name="discontinuity">El dispositivo informa de un hueco antes de este paquete (glitch de captura).</param>
public delegate void CaptureDataHandler(ReadOnlySpan<float> interleaved, bool discontinuity);

/// <summary>
/// Rellena <paramref name="interleaved"/> en el hilo de render del dispositivo. Siempre debe escribir el span completo
/// (silencio si no hay datos). Mismas restricciones que <see cref="CaptureDataHandler"/>.
/// </summary>
public delegate void RenderHandler(Span<float> interleaved);

/// <summary>
/// Stream de audio abierto sobre un endpoint concreto. Es de un solo uso: tras <see cref="Stopped"/> se desecha y se
/// abre otro. Siempre trabaja en float32 entrelazado con el <see cref="Format"/> pedido.
/// </summary>
public interface IAudioStream : IDisposable
{
    AudioFormat Format { get; }

    string DeviceName { get; }

    /// <summary>Latencia de buffer que informa el dispositivo, en milisegundos.</summary>
    double ReportedLatencyMilliseconds { get; }

    /// <summary>
    /// Se dispara una vez cuando el stream se detiene. Con excepción indica un fallo (p. ej. dispositivo desconectado);
    /// sin ella, una parada solicitada. Puede llegar en cualquier hilo.
    /// </summary>
    event Action<Exception?>? Stopped;

    void Start();

    void Stop();
}

public interface ICaptureStreamFactory
{
    /// <param name="deviceId">Id de endpoint, o <c>null</c> para el dispositivo de captura predeterminado de Windows.</param>
    /// <exception cref="AudioDeviceUnavailableException">El dispositivo no existe o no se puede abrir.</exception>
    IAudioStream Open(string? deviceId, AudioFormat format, int bufferMilliseconds, CaptureDataHandler onData);
}

public interface IRenderStreamFactory
{
    /// <param name="deviceId">Id de endpoint, o <c>null</c> para el dispositivo de salida predeterminado de Windows.</param>
    /// <exception cref="AudioDeviceUnavailableException">El dispositivo no existe o no se puede abrir.</exception>
    IAudioStream Open(string? deviceId, AudioFormat format, int latencyMilliseconds, RenderHandler render);
}

public sealed class AudioDeviceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
