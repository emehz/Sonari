using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using Sonari.Audio.Core.Devices;
using Sonari.Audio.Core.Endpoints;

namespace Sonari.Audio.Devices;

/// <summary>
/// Traduce las notificaciones de endpoints de Windows a <see cref="EndpointChange"/>. Los eventos llegan en un hilo
/// del sistema de audio (sin contexto de sincronización), y los suscriptores no deben bloquearlo.
/// </summary>
public sealed class WasapiDeviceWatcher : IAudioDeviceWatcher, IDisposable
{
    private readonly ILogger<WasapiDeviceWatcher> _logger;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _notifications;

    public WasapiDeviceWatcher(ILogger<WasapiDeviceWatcher> logger)
    {
        _logger = logger;
        _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _notifications.DeviceAdded += (_, e) => Raise(EndpointChangeKind.Added, e.DeviceId, null);
        _notifications.DeviceRemoved += (_, e) => Raise(EndpointChangeKind.Removed, e.DeviceId, null);
        _notifications.DeviceStateChanged += (_, e) => Raise(EndpointChangeKind.StateChanged, e.DeviceId, null);
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            // El motor usa el rol Multimedia («dispositivo predeterminado»); los cambios de otros roles no le afectan.
            if (e.Role == Role.Multimedia)
            {
                Raise(EndpointChangeKind.DefaultChanged, e.DeviceId,
                    e.Flow == DataFlow.Capture ? EndpointFlow.Capture : EndpointFlow.Render);
            }
        };
    }

    public event EventHandler<EndpointChange>? Changed;

    public void Dispose()
    {
        _notifications.Dispose();
        _enumerator.Dispose();
    }

    private void Raise(EndpointChangeKind kind, string? deviceId, EndpointFlow? flow)
    {
        _logger.LogDebug("Cambio de endpoint: {Kind} {DeviceId} {Flow}", kind, deviceId, flow);
        try
        {
            Changed?.Invoke(this, new EndpointChange(kind, deviceId, flow));
        }
        catch (Exception ex)
        {
            // Una excepción aquí volvería al hilo de notificaciones de Windows.
            _logger.LogError(ex, "Error al procesar un cambio de endpoint");
        }
    }
}
