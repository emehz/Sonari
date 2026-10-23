using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Capture;

/// <summary>
/// Application Loopback de Windows a través de NAudio (<c>WithProcessLoopback</c>, que llama a
/// <c>ActivateAudioInterfaceAsync</c> con <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>).
/// <para>
/// El dispositivo virtual de loopback no tiene formato de mezcla propio, así que se pide el del motor. No depende de
/// en qué salida suene la aplicación. Comprobado en Windows 10 build 19045: entrega paquetes de 10 ms aunque la
/// aplicación esté en silencio, mientras tenga un stream abierto.
/// </para>
/// </summary>
public sealed class WasapiProcessCaptureStreamFactory(ILoggerFactory loggerFactory) : IProcessCaptureStreamFactory
{
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public async Task<IAudioStream> OpenAsync(
        int processId,
        bool includeProcessTree,
        AudioFormat format,
        CaptureDataHandler onData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onData);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new AudioDeviceUnavailableException("Esta versión de Windows no permite capturar aplicaciones.");
        }

        ProcessLoopbackMode mode = includeProcessTree
            ? ProcessLoopbackMode.IncludeTargetProcessTree
            : ProcessLoopbackMode.ExcludeTargetProcessTree;

        WasapiRecorder recorder;
        try
        {
            recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)processId, mode)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels))
                .WithMmcssThreadPriority("Pro Audio")
                .BuildAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AudioDeviceUnavailableException($"Windows no permitió capturar el proceso {processId} ({ex.Message}).", ex);
        }

        string name = includeProcessTree ? $"Proceso {processId}" : "Sistema (excepto Sonari)";
        try
        {
            return new WasapiRecorderStream(recorder, format, name, onData, loggerFactory.CreateLogger<WasapiRecorderStream>());
        }
        catch
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
