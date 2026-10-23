namespace OndaMix.Audio.Core.Devices;

public enum EndpointFlow
{
    Capture,
    Render,
}

public enum EndpointState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
}

/// <summary>Instantánea inmutable de un endpoint de audio de Windows.</summary>
/// <param name="Id">Identificador estable del endpoint (cadena de MMDevice). Es lo que se persiste.</param>
/// <param name="Name">Nombre visible, p. ej. «Auriculares (Realtek(R) Audio)».</param>
/// <param name="IsLikelyVirtual">Coincide con un cable virtual conocido. Es una heurística por nombre, no una garantía.</param>
public sealed record AudioEndpointInfo(
    string Id,
    string Name,
    EndpointFlow Flow,
    EndpointState State,
    bool IsDefault,
    bool IsDefaultCommunications,
    bool IsLikelyVirtual);
