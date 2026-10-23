using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using OndaMix.Audio.Core.Devices;

namespace OndaMix.Audio.Devices;

/// <summary>
/// Enumera endpoints con la API MMDevice de Windows (vía NAudio). Cada llamada crea y libera su propio enumerador
/// COM, así que es segura desde cualquier hilo del pool (MTA). No llamar desde el hilo de UI.
/// </summary>
public sealed class WasapiDeviceCatalog(ILogger<WasapiDeviceCatalog> logger) : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioEndpointInfo> GetEndpoints(EndpointFlow flow, bool includeInactive = false)
    {
        DataFlow dataFlow = flow == EndpointFlow.Capture ? DataFlow.Capture : DataFlow.Render;
        DeviceState stateMask = includeInactive ? DeviceState.All : DeviceState.Active;

        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = TryGetDefaultId(enumerator, dataFlow, Role.Multimedia);
        string? defaultCommsId = TryGetDefaultId(enumerator, dataFlow, Role.Communications);

        var result = new List<AudioEndpointInfo>();
        using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(dataFlow, stateMask);
        foreach (MMDevice device in devices)
        {
            using (device)
            {
                string id = device.ID;
                string name = TryGetFriendlyName(device) ?? id;
                result.Add(new AudioEndpointInfo(
                    id,
                    name,
                    flow,
                    MapState(device.State),
                    IsDefault: id == defaultId,
                    IsDefaultCommunications: id == defaultCommsId,
                    IsLikelyVirtual: KnownVirtualDevices.IsLikelyVirtual(name)));
            }
        }

        logger.LogDebug("Enumerados {Count} endpoints de {Flow}", result.Count, flow);
        return result
            .OrderByDescending(e => e.IsDefault)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public AudioSystemCapabilities GetCapabilities() => AudioSystemCapabilities.FromOsVersion(Environment.OSVersion.Version);

    private string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            if (enumerator.TryGetDefaultAudioEndpoint(flow, role, out MMDevice? device) && device is not null)
            {
                using (device)
                {
                    return device.ID;
                }
            }
        }
        catch (COMException ex)
        {
            logger.LogWarning(ex, "No se pudo obtener el dispositivo predeterminado ({Flow}, {Role})", flow, role);
        }

        return null;
    }

    private string? TryGetFriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch (COMException ex)
        {
            // Los endpoints desconectados a veces no exponen su almacén de propiedades.
            logger.LogDebug(ex, "Endpoint sin nombre legible: {Id}", device.ID);
            return null;
        }
    }

    private static EndpointState MapState(DeviceState state) => state switch
    {
        DeviceState.Active => EndpointState.Active,
        DeviceState.Disabled => EndpointState.Disabled,
        DeviceState.Unplugged => EndpointState.Unplugged,
        _ => EndpointState.NotPresent,
    };
}
