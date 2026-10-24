using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Sonari.Audio.Core.Devices;
using Sonari.Audio.Core.Endpoints;

namespace Sonari.Audio.Devices;

/// <summary>Traduce un id de endpoint (o <c>null</c> = predeterminado) a un MMDevice activo, con errores comprensibles.</summary>
public static class WasapiEndpointResolver
{
    /// <summary>El llamador es dueño del dispositivo devuelto y debe liberarlo.</summary>
    /// <remarks>
    /// Los dispositivos del driver de Sonari nunca se usan como predeterminado ni como micrófono: Sonari se grabaría o se
    /// oiría a sí mismo en bucle. La salida del micrófono virtual siempre llega con su id, así que no le afecta.
    /// </remarks>
    /// <exception cref="AudioDeviceUnavailableException"/>
    public static MMDevice Resolve(MMDeviceEnumerator enumerator, string? deviceId, DataFlow flow)
    {
        string kind = flow == DataFlow.Capture ? "entrada" : "salida";
        try
        {
            if (deviceId is null)
            {
                return ResolveDefault(enumerator, flow)
                    ?? throw new AudioDeviceUnavailableException($"Windows no tiene ningún dispositivo de {kind} predeterminado que Sonari pueda usar.");
            }

            MMDevice selected = enumerator.GetDevice(deviceId);
            if (selected.State != DeviceState.Active)
            {
                string name = selected.FriendlyName;
                selected.Dispose();
                throw new AudioDeviceUnavailableException($"El dispositivo de {kind} «{name}» no está conectado o está deshabilitado.");
            }

            if (flow == DataFlow.Capture && SonariVirtualDevice.IsSonariName(selected.FriendlyName))
            {
                string name = selected.FriendlyName;
                selected.Dispose();
                throw new AudioDeviceUnavailableException(
                    $"«{name}» es el micrófono virtual de Sonari: como micrófono, Sonari se grabaría a sí mismo en bucle. Elige tu micrófono real.");
            }

            return selected;
        }
        catch (COMException ex)
        {
            throw new AudioDeviceUnavailableException($"No se encontró el dispositivo de {kind} seleccionado.", ex);
        }
    }

    /// <summary>La misma regla que <see cref="SonariVirtualDevice.EffectiveDefault"/>, sobre MMDevice.</summary>
    private static MMDevice? ResolveDefault(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        foreach (Role role in (Role[])[Role.Multimedia, Role.Communications])
        {
            if (enumerator.TryGetDefaultAudioEndpoint(flow, role, out MMDevice? device) && device is not null)
            {
                if (!SonariVirtualDevice.IsSonariName(device.FriendlyName))
                {
                    return device;
                }

                device.Dispose();
            }
        }

        // Windows solo tiene dispositivos de Sonari como predeterminados: el primer dispositivo real.
        MMDevice? fallback = null;
        foreach (MMDevice candidate in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            string name = candidate.FriendlyName;
            if (fallback is null && !SonariVirtualDevice.IsSonariName(name) && !KnownVirtualDevices.IsLikelyVirtual(name))
            {
                fallback = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }

        return fallback;
    }
}
