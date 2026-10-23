using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OndaMix.Infrastructure.Storage;
using OndaMix.Presentation.Views;
using Serilog;
using Serilog.Events;

namespace OndaMix.App;

public partial class App : Application
{
    // "Local\" limita la exclusión a la sesión del usuario: dos usuarios con sesión abierta pueden usar OndaMix a la vez.
    private const string SingleInstanceMutexName = @"Local\OndaMix.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Dos instancias se pelearían por los mismos dispositivos de audio.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("OndaMix ya está abierto.", "OndaMix", MessageBoxButton.OK, MessageBoxImage.Information);
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        AppPaths paths = AppPaths.ForCurrentUser();
        paths.EnsureCreated();
        Log.Logger = CreateLogger(paths);
        RegisterGlobalExceptionHandlers();

        Log.Information(
            "OndaMix {Version} arrancando · {Os} · {Runtime} · {Arch}",
            typeof(App).Assembly.GetName().Version?.ToString(3),
            Environment.OSVersion.VersionString,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture);

        try
        {
            _host = CompositionRoot.BuildHost(e.Args, paths);
            _host.Start();

            MainWindow window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fallo durante el arranque");
            MessageBox.Show(
                $"OndaMix no pudo arrancar:\n\n{ex.Message}\n\nHay más detalles en:\n{paths.LogDirectory}",
                "OndaMix", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                // Detiene el motor de audio (AudioEngineLifetime) antes de liberar los servicios.
                _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

                // El motor solo implementa IAsyncDisposable: la liberación síncrona del contenedor fallaría.
                ((IAsyncDisposable)_host).DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error al detener los servicios");
            }
        }

        Log.Information("OndaMix cerrado (código {ExitCode})", e.ApplicationExitCode);
        Log.CloseAndFlush();

        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private static Serilog.ILogger CreateLogger(AppPaths paths) =>
        new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "ondamix-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Excepción no controlada (terminando: {IsTerminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Excepción no observada en una tarea");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Un error de UI se registra y se muestra, pero no cierra la aplicación: el audio no debe cortarse por un fallo visual.
        Log.Error(e.Exception, "Excepción no controlada en el hilo de UI");
        MessageBox.Show(
            $"Se produjo un error inesperado:\n\n{e.Exception.Message}\n\nSe ha guardado en el registro.",
            "OndaMix", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
