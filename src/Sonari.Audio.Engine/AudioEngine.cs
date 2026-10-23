using System.Runtime;
using Microsoft.Extensions.Logging;
using OndaMix.Audio.Core.Devices;
using OndaMix.Audio.Core.Endpoints;
using OndaMix.Audio.Core.Engine;
using OndaMix.Audio.Engine.Routing;

namespace OndaMix.Audio.Engine;

/// <summary>
/// Motor de audio de la Fase 2: micrófono → compensación de deriva → bus de monitoreo → auriculares.
/// <para>
/// Un supervisor en segundo plano abre los dispositivos y los vuelve a abrir si fallan, si desaparecen o si cambia el
/// predeterminado de Windows (cuando el usuario no eligió uno concreto). El grafo de audio se crea una vez por
/// <see cref="StartAsync"/>, así que ganancias, contadores y medidores sobreviven a las recuperaciones.
/// </para>
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeviceChangeDebounce = TimeSpan.FromMilliseconds(300);

    private readonly ICaptureStreamFactory _captureFactory;
    private readonly IRenderStreamFactory _renderFactory;
    private readonly IAudioDeviceWatcher? _watcher;
    private readonly ILogger<AudioEngine> _logger;
    private readonly ChannelControl _microphone = new();
    private readonly BusControl _monitor = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    private EngineStatus _status = EngineStatus.Stopped;
    private EngineOptions? _options;
    private MixerChannel? _micChannel;
    private MonitorBus? _monitorBus;
    private IAudioStream? _capture;
    private IAudioStream? _render;
    private CancellationTokenSource? _runCts;
    private Task? _supervisor;
    private GCLatencyMode? _previousGcMode;
    private int _generation;
    private int _consecutiveOpenFailures;
    private int _streamFailed;
    private int _defaultDeviceChanged;

    public AudioEngine(
        ICaptureStreamFactory captureFactory,
        IRenderStreamFactory renderFactory,
        IAudioDeviceWatcher? watcher,
        ILogger<AudioEngine> logger)
    {
        _captureFactory = captureFactory;
        _renderFactory = renderFactory;
        _watcher = watcher;
        _logger = logger;

        // Por seguridad frente a la realimentación, el micrófono no se oye hasta que el usuario lo pide.
        _microphone.SendToMonitor = false;

        if (_watcher is not null)
        {
            _watcher.Changed += OnEndpointChanged;
        }
    }

    public EngineStatus Status => Volatile.Read(ref _status);

    public event EventHandler<EngineStatus>? StatusChanged;

    public IChannelControl Microphone => _microphone;

    public IBusControl Monitor => _monitor;

    public async Task StartAsync(EngineOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            _options = options;
            int targetFrames = options.Format.FramesFor(TimeSpan.FromMilliseconds(TargetBufferMilliseconds(options)));
            _micChannel = new MixerChannel(_microphone, options.Format, targetFrames);
            _monitorBus = new MonitorBus(_monitor, options.Format.Channels, _micChannel);

            SetStatus(new EngineStatus(EngineState.Starting, "Abriendo dispositivos…"));
            _logger.LogInformation(
                "Arrancando motor: {Format}, bloque {Block} ms, entrada {Input}, monitoreo {Monitor}",
                options.Format, options.BlockMilliseconds, options.InputDeviceId ?? "(predeterminada)",
                options.MonitorDeviceId ?? "(predeterminada)");

            _previousGcMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            _runCts = new CancellationTokenSource();
            CancellationToken token = _runCts.Token;

            // Abrir dispositivos es COM y puede tardar: nunca en el hilo del llamador (normalmente la UI).
            await Task.Run(TryOpenStreams, CancellationToken.None).ConfigureAwait(false);
            _supervisor = Task.Run(() => SuperviseAsync(token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public EngineMetrics ReadMetrics()
    {
        MixerChannel? channel = Volatile.Read(ref _micChannel);
        MonitorBus? bus = Volatile.Read(ref _monitorBus);
        EngineOptions? options = Volatile.Read(ref _options);
        if (channel is null || bus is null || options is null)
        {
            return default;
        }

        DriftCompensatingReader reader = channel.MonitorReader;
        return new EngineMetrics(
            MicrophoneInput: channel.InputMeter.ReadAndReset(),
            MicrophonePostFader: channel.PostFaderMeter.ReadAndReset(),
            MonitorOutput: bus.OutputMeter.ReadAndReset(),
            MonitorBufferMilliseconds: reader.IsPriming ? 0 : reader.SmoothedFillFrames * 1000.0 / options.Format.SampleRate,
            MonitorDriftPpm: reader.CorrectionPpm,
            Underruns: reader.Underruns,
            OverrunSamples: channel.OverrunSamples,
            Resyncs: reader.Resyncs,
            CaptureDiscontinuities: channel.Discontinuities,
            ClippedSamples: bus.ClippedSamples);
    }

    public async ValueTask DisposeAsync()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnEndpointChanged;
        }

        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
        _wake.Dispose();
    }

    /// <summary>Margen del buffer de compensación: al menos un periodo de captura (10 ms) más la mitad de holgura.</summary>
    internal static double TargetBufferMilliseconds(EngineOptions options) => Math.Max(10, options.BlockMilliseconds) * 1.5;

    internal static int RenderLatencyMilliseconds(EngineOptions options) => Math.Max(10, options.BlockMilliseconds * 2);

    internal static int CaptureBufferMilliseconds(EngineOptions options) => Math.Max(50, options.BlockMilliseconds * 4);

    private async Task StopCoreAsync()
    {
        if (_runCts is null)
        {
            return;
        }

        await _runCts.CancelAsync().ConfigureAwait(false);
        _wake.Release();
        if (_supervisor is not null)
        {
            try
            {
                await _supervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Esperado al cancelar.
            }
        }

        await Task.Run(CloseStreams).ConfigureAwait(false);

        _runCts.Dispose();
        _runCts = null;
        _supervisor = null;
        Volatile.Write(ref _micChannel, null);
        Volatile.Write(ref _monitorBus, null);
        Volatile.Write(ref _options, null);

        if (_previousGcMode is { } mode)
        {
            GCSettings.LatencyMode = mode;
            _previousGcMode = null;
        }

        _logger.LogInformation("Motor detenido");
        SetStatus(EngineStatus.Stopped);
    }

    private async Task SuperviseAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool healthy = _capture is not null && _render is not null && Volatile.Read(ref _streamFailed) == 0;
            try
            {
                await _wake.WaitAsync(healthy ? Timeout.InfiniteTimeSpan : RetryInterval, token).ConfigureAwait(false);

                // Windows envía ráfagas de avisos por un solo cambio (uno por rol): se agrupan.
                await Task.Delay(DeviceChangeDebounce, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (_wake.CurrentCount > 0)
            {
                _wake.Wait(0, CancellationToken.None);
            }

            bool failed = Interlocked.Exchange(ref _streamFailed, 0) != 0;
            bool defaultChanged = Interlocked.Exchange(ref _defaultDeviceChanged, 0) != 0;
            healthy = _capture is not null && _render is not null;

            if (failed || !healthy || defaultChanged)
            {
                if (defaultChanged && healthy && !failed)
                {
                    _logger.LogInformation("Cambió el dispositivo predeterminado de Windows; reabriendo");
                }

                CloseStreams();
                TryOpenStreams();
            }
        }
    }

    private bool TryOpenStreams()
    {
        EngineOptions options = _options ?? throw new InvalidOperationException("Motor sin configurar.");
        MonitorBus bus = _monitorBus!;
        MixerChannel channel = _micChannel!;
        int generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _streamFailed, 0);
        bus.ResetForRestart();

        try
        {
            _render = _renderFactory.Open(
                options.MonitorDeviceId, options.Format, RenderLatencyMilliseconds(options), bus.Render);
            _render.Stopped += ex => OnStreamStopped(generation, "salida de monitoreo", ex);

            _capture = _captureFactory.Open(
                options.InputDeviceId, options.Format, CaptureBufferMilliseconds(options), channel.OnCaptured);
            _capture.Stopped += ex => OnStreamStopped(generation, "micrófono", ex);

            _render.Start();
            _capture.Start();

            _logger.LogInformation(
                "Motor en marcha: entrada «{Input}» (buffer {InLatency} ms), salida «{Output}» ({OutLatency} ms)",
                _capture.DeviceName, _capture.ReportedLatencyMilliseconds,
                _render.DeviceName, _render.ReportedLatencyMilliseconds);

            SetStatus(new EngineStatus(
                EngineState.Running,
                "En marcha",
                _capture.DeviceName,
                _render.DeviceName,
                _capture.ReportedLatencyMilliseconds,
                _render.ReportedLatencyMilliseconds));
            _consecutiveOpenFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            // Cualquier fallo al abrir se trata igual: cerrar lo abierto, informar y reintentar más tarde.
            // Solo el primer fallo de una racha va como aviso, para no llenar el registro cada 2 s.
            _logger.Log(
                _consecutiveOpenFailures++ == 0 ? LogLevel.Warning : LogLevel.Debug,
                ex,
                "No se pudieron abrir los dispositivos de audio (intento {Attempt})",
                _consecutiveOpenFailures);
            CloseStreams();
            SetStatus(new EngineStatus(EngineState.Recovering, $"{ex.Message} Reintentando cada {RetryInterval.TotalSeconds:0} s…"));
            return false;
        }
    }

    private void CloseStreams()
    {
        // Primero la captura (productor) y luego la salida (consumidor).
        DisposeQuietly(ref _capture, "micrófono");
        DisposeQuietly(ref _render, "salida de monitoreo");
    }

    private void DisposeQuietly(ref IAudioStream? stream, string role)
    {
        IAudioStream? current = stream;
        stream = null;
        if (current is null)
        {
            return;
        }

        try
        {
            current.Dispose();
        }
        catch (Exception ex)
        {
            // Un dispositivo ya desconectado suele fallar al cerrarse; no debe impedir la recuperación.
            _logger.LogDebug(ex, "Error al cerrar {Role}", role);
        }
    }

    private void OnStreamStopped(int generation, string role, Exception? exception)
    {
        if (exception is null || generation != Volatile.Read(ref _generation))
        {
            // Parada solicitada, o aviso tardío de un stream que ya se sustituyó.
            return;
        }

        _logger.LogWarning(exception, "Se detuvo inesperadamente: {Role}", role);
        Volatile.Write(ref _streamFailed, 1);
        SetStatus(new EngineStatus(EngineState.Recovering, $"Se perdió el {role}. Reintentando…"));
        _wake.Release();
    }

    private void OnEndpointChanged(object? sender, EndpointChange change)
    {
        EngineOptions? options = Volatile.Read(ref _options);
        if (options is null)
        {
            return;
        }

        bool followsDefault = change.Kind == EndpointChangeKind.DefaultChanged &&
            ((change.Flow == EndpointFlow.Capture && options.InputDeviceId is null) ||
             (change.Flow == EndpointFlow.Render && options.MonitorDeviceId is null));

        if (followsDefault)
        {
            Volatile.Write(ref _defaultDeviceChanged, 1);
        }

        // Con el motor sano, el supervisor ignora los avisos que no le afectan; en recuperación, reintenta enseguida.
        _wake.Release();
    }

    private void SetStatus(EngineStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, status);
    }
}
