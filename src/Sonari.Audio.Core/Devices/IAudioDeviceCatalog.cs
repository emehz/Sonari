namespace Sonari.Audio.Core.Devices;

/// <summary>Consulta de endpoints de audio. Las implementaciones pueden bloquear un momento: no llamar desde el hilo de UI.</summary>
public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioEndpointInfo> GetEndpoints(EndpointFlow flow, bool includeInactive = false);

    AudioSystemCapabilities GetCapabilities();
}
