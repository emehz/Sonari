namespace Sonari.Audio.Core.Devices;

/// <summary>
/// Endpoints del driver propio de Sonari (carpeta <c>driver/</c>): la mezcla se escribe en
/// «Sonari Virtual Output (Sonari Virtual Audio)» y Discord y el resto graban de
/// «Sonari Virtual Microphone (Sonari Virtual Audio)».
/// Se reconocen por el nombre del dispositivo, el texto entre paréntesis, que fija el INF del driver.
/// El nombre del endpoint, la parte de delante, lo puede cambiar el usuario en el panel de sonido.
/// </summary>
public static class SonariVirtualDevice
{
    /// <summary>Descripción del dispositivo en el INF del driver (<c>SIMPLEAUDIOSAMPLE_SA.DeviceDesc</c>).</summary>
    public const string DeviceName = "Sonari Virtual Audio";

    public static bool IsSonari(AudioEndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return IsSonariName(endpoint.Name);
    }

    /// <param name="endpointName">Nombre visible del endpoint, p. ej. «Sonari Virtual Microphone (Sonari Virtual Audio)».</param>
    public static bool IsSonariName(string? endpointName) =>
        !string.IsNullOrWhiteSpace(endpointName) &&
        string.Equals(VirtualCablePairing.DeviceFamily(endpointName), DeviceName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dispositivo que Sonari usa cuando el micrófono o el monitoreo están en «Predeterminado de Windows». Nunca es uno de
    /// Sonari, porque Sonari se grabaría o se oiría a sí mismo en bucle: si Windows tiene uno de Sonari como predeterminado,
    /// se usa el predeterminado de comunicaciones o, si también es de Sonari, el primer dispositivo que no sea virtual.
    /// <c>WasapiEndpointResolver</c> aplica la misma regla al abrir el dispositivo.
    /// </summary>
    /// <param name="endpoints">Endpoints activos de un mismo sentido (entrada o salida).</param>
    public static AudioEndpointInfo? EffectiveDefault(IEnumerable<AudioEndpointInfo> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        IReadOnlyList<AudioEndpointInfo> active = endpoints.Where(e => e.State == EndpointState.Active).ToList();
        return active.FirstOrDefault(e => e.IsDefault && !IsSonari(e))
            ?? active.FirstOrDefault(e => e.IsDefaultCommunications && !IsSonari(e))
            ?? active.FirstOrDefault(e => !IsSonari(e) && !e.IsLikelyVirtual);
    }

    /// <summary>
    /// Salida que el mezclador debe elegir por su cuenta para el micrófono virtual, o <c>null</c> para respetar la elección
    /// actual. Solo elige la del driver de Sonari, y solo si el usuario no eligió nada (ni siquiera «Desactivado») o si lo
    /// que eligió ya no existe, p. ej. porque desinstaló VB-CABLE.
    /// </summary>
    /// <param name="savedOutputId">Salida guardada en los ajustes; <c>null</c> = desactivado o nunca elegido.</param>
    /// <param name="userChose">El usuario cambió alguna vez la salida del micrófono virtual.</param>
    public static AudioEndpointInfo? AutoSelectOutput(
        IEnumerable<AudioEndpointInfo> outputs, string? savedOutputId, bool userChose)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        IReadOnlyList<AudioEndpointInfo> renders = outputs.Where(o => o.Flow == EndpointFlow.Render).ToList();
        AudioEndpointInfo? sonari = renders.FirstOrDefault(o => o.State == EndpointState.Active && IsSonari(o));
        if (sonari is null)
        {
            return null;
        }

        return savedOutputId is null
            ? (userChose ? null : sonari)
            : (renders.Any(o => o.Id == savedOutputId) ? null : sonari);
    }
}
