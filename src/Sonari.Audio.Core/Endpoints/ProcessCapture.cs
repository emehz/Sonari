using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Core.Endpoints;

/// <summary>Captura del audio que renderiza un árbol de procesos (Application Loopback de Windows).</summary>
public interface IProcessCaptureStreamFactory
{
    /// <summary>Falso si esta versión de Windows no ofrece Application Loopback (anterior a Windows 10 2004).</summary>
    bool IsSupported { get; }

    /// <param name="includeProcessTree">
    /// <c>true</c>: solo el audio de ese proceso y sus hijos. <c>false</c>: todo el audio del sistema excepto ese árbol.
    /// </param>
    /// <exception cref="AudioDeviceUnavailableException">Windows no permitió la captura.</exception>
    Task<IAudioStream> OpenAsync(
        int processId,
        bool includeProcessTree,
        AudioFormat format,
        CaptureDataHandler onData,
        CancellationToken cancellationToken = default);
}

/// <summary>Localiza procesos por ejecutable. Las implementaciones no deben bloquear más de unos milisegundos.</summary>
public interface IProcessLocator
{
    int CurrentProcessId { get; }

    /// <summary>
    /// Proceso raíz en ejecución de ese ejecutable (el más alto del árbol con el mismo ejecutable, p. ej. el proceso
    /// principal de un navegador). Si <paramref name="preferredProcessId"/> sigue vivo y es una raíz válida, se elige ese.
    /// </summary>
    int? FindRootProcess(string executablePath, int? preferredProcessId);

    bool IsRunning(int processId);
}
