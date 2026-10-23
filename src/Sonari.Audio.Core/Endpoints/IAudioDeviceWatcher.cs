using Sonari.Audio.Core.Devices;

namespace Sonari.Audio.Core.Endpoints;

public enum EndpointChangeKind
{
    Added,
    Removed,
    StateChanged,
    DefaultChanged,
}

/// <param name="Flow">Solo se conoce en <see cref="EndpointChangeKind.DefaultChanged"/>.</param>
public sealed record EndpointChange(EndpointChangeKind Kind, string? DeviceId, EndpointFlow? Flow);

/// <summary>Avisos de Windows cuando se conecta, desconecta o cambia el dispositivo predeterminado.</summary>
public interface IAudioDeviceWatcher
{
    /// <summary>Puede dispararse en cualquier hilo y varias veces seguidas para un mismo cambio físico.</summary>
    event EventHandler<EndpointChange>? Changed;
}
