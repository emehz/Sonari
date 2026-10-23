using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using OndaMix.Audio.Core.Endpoints;

namespace OndaMix.Audio.Devices;

/// <summary>Traduce un id de endpoint (o <c>null</c> = predeterminado) a un MMDevice activo, con errores comprensibles.</summary>
public static class WasapiEndpointResolver
{
    /// <summary>El llamador es dueño del dispositivo devuelto y debe liberarlo.</summary>
    /// <exception cref="AudioDeviceUnavailableException"/>
    public static MMDevice Resolve(MMDeviceEnumerator enumerator, string? deviceId, DataFlow flow)
    {
        string kind = flow == DataFlow.Capture ? "entrada" : "salida";
        try
        {
            if (deviceId is null)
            {
                if (enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out MMDevice? device) && device is not null)
                {
                    return device;
                }

                throw new AudioDeviceUnavailableException($"Windows no tiene ningún dispositivo de {kind} predeterminado.");
            }

            MMDevice selected = enumerator.GetDevice(deviceId);
            if (selected.State != DeviceState.Active)
            {
                string name = selected.FriendlyName;
                selected.Dispose();
                throw new AudioDeviceUnavailableException($"El dispositivo de {kind} «{name}» no está conectado o está deshabilitado.");
            }

            return selected;
        }
        catch (COMException ex)
        {
            throw new AudioDeviceUnavailableException($"No se encontró el dispositivo de {kind} seleccionado.", ex);
        }
    }
}
