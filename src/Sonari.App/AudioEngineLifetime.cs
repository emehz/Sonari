using Microsoft.Extensions.Hosting;
using Sonari.Audio.Core.Engine;

namespace Sonari.App;

/// <summary>Garantiza que el motor suelta los dispositivos al cerrar la aplicación, pase lo que pase en la UI.</summary>
internal sealed class AudioEngineLifetime(IAudioEngine engine) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => engine.StopAsync();
}
