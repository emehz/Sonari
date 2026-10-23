using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OndaMix.Audio.Capture;
using OndaMix.Audio.Core.Devices;
using OndaMix.Audio.Core.Endpoints;
using OndaMix.Audio.Core.Engine;
using OndaMix.Audio.Devices;
using OndaMix.Audio.Engine;
using OndaMix.Audio.Output;
using OndaMix.Infrastructure.Settings;
using OndaMix.Infrastructure.Storage;
using OndaMix.Presentation.Services;
using OndaMix.Presentation.ViewModels;
using OndaMix.Presentation.Views;
using Serilog;

namespace OndaMix.App;

/// <summary>Registro de todos los servicios. Es el único lugar que une interfaces con implementaciones.</summary>
internal static class CompositionRoot
{
    public static IHost BuildHost(string[] args, AppPaths paths)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = "OndaMix",
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger, dispose: false);
#if DEBUG
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        IServiceCollection services = builder.Services;

        // Infraestructura
        services.AddSingleton(paths);
        services.AddSingleton<JsonFileStore>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // Audio
        services.AddSingleton<IAudioDeviceCatalog, WasapiDeviceCatalog>();
        services.AddSingleton<IAudioDeviceWatcher, WasapiDeviceWatcher>();
        services.AddSingleton<ICaptureStreamFactory, WasapiCaptureStreamFactory>();
        services.AddSingleton<IRenderStreamFactory, WasapiRenderStreamFactory>();
        services.AddSingleton<IAudioEngine, AudioEngine>();
        services.AddHostedService<AudioEngineLifetime>();

        // Presentación. El orden de registro de las páginas es el orden de la navegación.
        services.AddSingleton<IShellIntegration, ShellIntegration>();
        services.AddSingleton<MixerViewModel>();
        services.AddSingleton<PageViewModel>(sp => sp.GetRequiredService<MixerViewModel>());
        foreach (PlannedPageViewModel page in PlannedPageViewModel.CreateDefaults())
        {
            services.AddSingleton<PageViewModel>(page);
        }

        services.AddSingleton<DevicesViewModel>();
        services.AddSingleton<PageViewModel>(sp => sp.GetRequiredService<DevicesViewModel>());
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<PageViewModel>(sp => sp.GetRequiredService<SettingsViewModel>());

        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();

        return builder.Build();
    }
}
