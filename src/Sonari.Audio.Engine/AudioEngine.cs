using System.Runtime;
using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Devices;
using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;
using Sonari.Audio.Engine.Playback;
using Sonari.Audio.Engine.Routing;

namespace Sonari.Audio.Engine;

/// <summary>
/// Motor de audio con dos mezclas independientes:
/// <list type="bullet">
/// <item><b>Monitoreo</b>: lo que oyes por los auriculares.</item>
/// <item><b>Micrófono virtual</b> (opcional): lo que oyen los demás, escrito en la entrada de un cable virtual.</item>
/// </list>
/// <para>
/// Cada fuente (micrófono, aplicaciones, sistema, sonidos) envía por separado a cada mezcla, y cada mezcla la marca el
/// reloj de su propio dispositivo. Un supervisor abre micrófono y monitoreo y los reabre si fallan, desaparecen o cambia
/// el predeterminado de Windows. La salida virtual es opcional: si falta o falla, el resto sigue funcionando y se
/// reintenta por separado, sin cortar el monitoreo. Cada aplicación tiene su propio bucle de captura
/// (<see cref="ProcessCaptureRunner"/>). Los controles viven fuera de los arranques, así que ganancias y envíos se conservan.
/// </para>
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeviceChangeDebounce = TimeSpan.FromMilliseconds(300);

    private readonly ICaptureStreamFactory _captureFactory;
    private readonly IRenderStreamFactory _renderFactory;
    private readonly IProcessCaptureStreamFactory _processCaptureFactory;
    private readonly IProcessLocator _processLocator;
    private readonly IAudioDeviceWatcher? _watcher;
    private readonly ILogger<AudioEngine> _logger;
    private readonly SourceChannel _microphone;
    private readonly SourceChannel _soundsSource;
    private readonly SoundPlayer _soundPlayer = new();
    private readonly BusControl _monitor = new();
    private readonly BusControl _virtual = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    // Protege _sources, _runners, los buses y _running frente a añadir/quitar fuentes desde la UI durante un arranque.
    private readonly object _sourcesLock = new();
    private readonly Dictionary<Guid, ProcessCaptureRunner> _runners = [];
    private SourceChannel[] _sources;
    private bool _running;

    private EngineStatus _status = EngineStatus.Stopped;
    private EngineOptions? _options;
    private int _targetFrames;
    private MixBus? _monitorBus;
    private MixBus? _virtualBus;
    private IAudioStream? _capture;
    private IAudioStream? _render;
    private IAudioStream? _virtualRender;
    private string? _virtualMessage;
    private CancellationTokenSource? _runCts;
    private Task? _supervisor;
    private GCLatencyMode? _previousGcMode;
    private int _generation;
    private int _virtualGeneration;
    private int _consecutiveOpenFailures;
    private int _consecutiveVirtualFailures;
    private int _streamFailed;
    private int _virtualFailed;
    private int _defaultDeviceChanged;
    private int _globalMuted;

    public AudioEngine(
        ICaptureStreamFactory captureFactory,
        IRenderStreamFactory renderFactory,
        IProcessCaptureStreamFactory processCaptureFactory,
        IProcessLocator processLocator,
        IAudioDeviceWatcher? watcher,
        ILogger<AudioEngine> logger)
    {
        _captureFactory = captureFactory;
        _renderFactory = renderFactory;
        _processCaptureFactory = processCaptureFactory;
        _processLocator = processLocator;
        _watcher = watcher;
        _logger = logger;

        // Por seguridad frente a la realimentación, el micrófono no se oye hasta que el usuario lo pide.
        _microphone = new SourceChannel(SourceKind.Microphone, "Micrófono", target: null, RaiseSourcesChanged);

        // Los sonidos sí se oyen por defecto: son efectos que lanzas tú y no tienen otra vía para llegar a tus oídos.
        _soundsSource = new SourceChannel(SourceKind.SoundPlayer, "Sonidos", target: null, RaiseSourcesChanged);
        _soundsSource.SendToMonitor = true;
        _sources = [_microphone, _soundsSource];

        if (_watcher is not null)
        {
            _watcher.Changed += OnEndpointChanged;
        }
    }

    public EngineStatus Status => Volatile.Read(ref _status);

    public AudioFormat? Format => Volatile.Read(ref _options)?.Format;

    public event EventHandler<EngineStatus>? StatusChanged;

    public event EventHandler? SourcesChanged;

    public ISourceChannel Microphone => _microphone;

    public ISourceChannel SoundChannel => _soundsSource;

    public ISoundPlayer Sounds => _soundPlayer;

    public bool IsGlobalMuted
    {
        get => Volatile.Read(ref _globalMuted) != 0;
        set
        {
            if (Interlocked.Exchange(ref _globalMuted, value ? 1 : 0) != (value ? 1 : 0))
            {
                _logger.LogInformation("Silencio global {State}", value ? "activado" : "desactivado");
                RaiseSourcesChanged();
            }
        }
    }

    public IReadOnlyList<ISourceChannel> Sources => Volatile.Read(ref _sources);

    public IBusControl Monitor => _monitor;

    public IBusControl VirtualMicrophone => _virtual;

    public bool SupportsProcessCapture => _processCaptureFactory.IsSupported;

    public async Task StartAsync(EngineOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            SetStatus(new EngineStatus(EngineState.Starting, "Abriendo dispositivos…"));
            _logger.LogInformation(
                "Arrancando motor: {Format}, bloque {Block} ms, entrada {Input}, monitoreo {Monitor}, micrófono virtual {Virtual}",
                options.Format, options.BlockMilliseconds, options.InputDeviceId ?? "(predeterminada)",
                options.MonitorDeviceId ?? "(predeterminada)", options.VirtualOutputDeviceId ?? "(desactivado)");

            lock (_sourcesLock)
            {
                Volatile.Write(ref _options, options);
                _targetFrames = options.Format.FramesFor(TimeSpan.FromMilliseconds(TargetBufferMilliseconds(options)));
                _monitorBus = new MixBus(BusKind.Monitor, _monitor, options.Format, () => IsGlobalMuted);
                _virtualBus = options.VirtualOutputDeviceId is null
                    ? null
                    : new MixBus(BusKind.Virtual, _virtual, options.Format, () => IsGlobalMuted);
                _virtualMessage = options.VirtualOutputDeviceId is null ? "Micrófono virtual desactivado." : null;
                _soundPlayer.Configure(options.Format);

                foreach (SourceChannel source in _sources)
                {
                    source.Feed = CreateFeed(source);
                }

                PublishBusInputs();
                _running = true;
            }

            _previousGcMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            _runCts = new CancellationTokenSource();
            CancellationToken token = _runCts.Token;

            // Abrir dispositivos es COM y puede tardar: nunca en el hilo del llamador (normalmente la UI).
            await Task.Run(TryOpenStreams, CancellationToken.None).ConfigureAwait(false);
            _supervisor = Task.Run(() => SuperviseAsync(token), CancellationToken.None);

            lock (_sourcesLock)
            {
                foreach (SourceChannel source in _sources)
                {
                    if (IsProcessSource(source.Kind))
                    {
                        StartRunner(source);
                    }
                }
            }
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

    public ISourceChannel AddApplicationSource(ApplicationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfProcessCaptureUnsupported();

        // Ya oyes la aplicación directamente: enviarla también al monitoreo la duplicaría con retardo.
        var source = new SourceChannel(SourceKind.Application, target.DisplayName, target, RaiseSourcesChanged);
        AddSource(source);
        return source;
    }

    public ISourceChannel AddSystemAudioSource()
    {
        ThrowIfProcessCaptureUnsupported();
        lock (_sourcesLock)
        {
            SourceChannel? existing = _sources.FirstOrDefault(s => s.Kind == SourceKind.SystemAudio);
            if (existing is not null)
            {
                return existing;
            }
        }

        var source = new SourceChannel(SourceKind.SystemAudio, "Audio del sistema", target: null, RaiseSourcesChanged);
        AddSource(source);
        return source;
    }

    public void RemoveSource(ISourceChannel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsProcessSource(source.Kind))
        {
            throw new InvalidOperationException($"«{source.DisplayName}» forma parte fija del mezclador y no se puede quitar.");
        }

        ProcessCaptureRunner? runner;
        lock (_sourcesLock)
        {
            if (!_sources.Any(s => s.Id == source.Id))
            {
                return;
            }

            Volatile.Write(ref _sources, _sources.Where(s => s.Id != source.Id).ToArray());
            _runners.Remove(source.Id, out runner);
            if (_running)
            {
                PublishBusInputs();
            }
        }

        _logger.LogInformation("Fuente quitada: {Source}", source);
        RaiseSourcesChanged();

        if (runner is not null)
        {
            _ = StopRunnerAsync(runner, (SourceChannel)source);
        }
    }

    public EngineMetrics ReadMetrics()
    {
        MixBus? monitor = Volatile.Read(ref _monitorBus);
        if (monitor is null)
        {
            return default;
        }

        MixBus? virtualBus = Volatile.Read(ref _virtualBus);
        return new EngineMetrics(
            MonitorOutput: monitor.OutputMeter.ReadAndReset(),
            MonitorGainReductionDb: monitor.Limiter.ReadAndResetGainReductionDb(),
            ClippedSamples: monitor.ClippedSamples + (virtualBus?.ClippedSamples ?? 0),
            Resyncs: monitor.Resyncs + (virtualBus?.Resyncs ?? 0),
            VirtualOutput: virtualBus?.OutputMeter.ReadAndReset() ?? default,
            VirtualGainReductionDb: virtualBus?.Limiter.ReadAndResetGainReductionDb() ?? 0f);
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

    private static bool IsProcessSource(SourceKind kind) => kind is SourceKind.Application or SourceKind.SystemAudio;

    private void AddSource(SourceChannel source)
    {
        lock (_sourcesLock)
        {
            Volatile.Write(ref _sources, [.. _sources, source]);
            if (_running)
            {
                source.Feed = CreateFeed(source);
                PublishBusInputs();
                StartRunner(source);
            }
        }

        _logger.LogInformation("Fuente añadida: {Source}", source);
        RaiseSourcesChanged();
    }

    /// <summary>Con <see cref="_sourcesLock"/> tomado y las opciones fijadas.</summary>
    private SourceFeed CreateFeed(SourceChannel source)
    {
        var feed = new SourceFeed(
            source.Control,
            _options!.Format,
            _targetFrames,
            source.Kind,
            withVirtualBus: _virtualBus is not null,
            generator: source.Kind == SourceKind.SoundPlayer ? _soundPlayer.Render : null);

        feed.PrimaryBus = _virtualRender is not null ? BusKind.Virtual : BusKind.Monitor;
        return feed;
    }

    /// <summary>Con <see cref="_sourcesLock"/> tomado.</summary>
    private void PublishBusInputs()
    {
        SourceFeed[] feeds = _sources.Select(s => s.Feed).OfType<SourceFeed>().ToArray();
        _monitorBus?.SetInputs(feeds.Select(f => f.MonitorInput).ToArray());
        _virtualBus?.SetInputs(feeds.Select(f => f.VirtualInput!).ToArray());
    }

    /// <summary>Con <see cref="_sourcesLock"/> tomado y el motor en marcha.</summary>
    private void StartRunner(SourceChannel source)
    {
        var runner = new ProcessCaptureRunner(
            source, source.Feed!, _options!.Format, _processCaptureFactory, _processLocator, _logger);
        _runners[source.Id] = runner;
        runner.Start();
    }

    private async Task StopRunnerAsync(ProcessCaptureRunner runner, SourceChannel source)
    {
        try
        {
            await runner.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error al detener la captura de {Source}", source);
        }

        source.Feed = null;
        source.SetStatus(SourceStatus.Stopped, "Motor detenido");
    }

    private void ThrowIfProcessCaptureUnsupported()
    {
        if (!SupportsProcessCapture)
        {
            throw new NotSupportedException(
                "Esta versión de Windows no permite capturar aplicaciones (requiere Windows 10 versión 2004 o posterior).");
        }
    }

    private async Task StopCoreAsync()
    {
        if (_runCts is null)
        {
            return;
        }

        List<(ProcessCaptureRunner Runner, SourceChannel Source)> runners;
        lock (_sourcesLock)
        {
            _running = false;
            runners = _runners.Select(r => (r.Value, _sources.First(s => s.Id == r.Key))).ToList();
            _runners.Clear();
        }

        await Task.WhenAll(runners.Select(r => StopRunnerAsync(r.Runner, r.Source))).ConfigureAwait(false);

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

        lock (_sourcesLock)
        {
            foreach (SourceChannel source in _sources)
            {
                source.Feed = null;
            }

            // Con el render ya parado: lo que sonaba se descarta para que la UI no muestre reproducciones fantasma.
            _soundPlayer.Configure(_options?.Format ?? AudioFormat.Default);
            Volatile.Write(ref _monitorBus, null);
            Volatile.Write(ref _virtualBus, null);
            Volatile.Write(ref _options, null);
        }

        _microphone.SetStatus(SourceStatus.Stopped, "Motor detenido");
        _soundsSource.SetStatus(SourceStatus.Stopped, "Motor detenido");

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
            bool mainHealthy = _capture is not null && _render is not null && Volatile.Read(ref _streamFailed) == 0;
            bool virtualPending = _virtualBus is not null && (_virtualRender is null || Volatile.Read(ref _virtualFailed) != 0);
            try
            {
                await _wake.WaitAsync(mainHealthy && !virtualPending ? Timeout.InfiniteTimeSpan : RetryInterval, token)
                    .ConfigureAwait(false);

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
            bool virtualFailed = Interlocked.Exchange(ref _virtualFailed, 0) != 0;
            mainHealthy = _capture is not null && _render is not null;

            if (failed || !mainHealthy || defaultChanged)
            {
                if (defaultChanged && mainHealthy && !failed)
                {
                    _logger.LogInformation("Cambió el dispositivo predeterminado de Windows; reabriendo");
                }

                CloseStreams();
                TryOpenStreams();
            }
            else if (_virtualBus is not null && (virtualFailed || _virtualRender is null))
            {
                // Solo la salida virtual: el micrófono y los auriculares siguen sonando sin cortes.
                CloseVirtual();
                TryOpenVirtual();
                PublishStatus();
            }
        }
    }

    private bool TryOpenStreams()
    {
        EngineOptions options = _options ?? throw new InvalidOperationException("Motor sin configurar.");
        MixBus monitorBus = _monitorBus!;
        SourceFeed micFeed = _microphone.Feed!;
        int generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _streamFailed, 0);
        SetPrimaryBus(BusKind.Monitor);
        monitorBus.ResetForRestart();

        try
        {
            _render = _renderFactory.Open(
                options.MonitorDeviceId, options.Format, RenderLatencyMilliseconds(options), monitorBus.Render);
            _render.Stopped += ex => OnStreamStopped(generation, "salida de monitoreo", ex);

            _capture = _captureFactory.Open(
                options.InputDeviceId, options.Format, CaptureBufferMilliseconds(options), micFeed.OnCaptured);
            _capture.Stopped += ex => OnStreamStopped(generation, "micrófono", ex);

            monitorBus.SetActive(true);
            _render.Start();
            _capture.Start();

            _logger.LogInformation(
                "Motor en marcha: entrada «{Input}» (buffer {InLatency} ms), salida «{Output}» ({OutLatency} ms)",
                _capture.DeviceName, _capture.ReportedLatencyMilliseconds,
                _render.DeviceName, _render.ReportedLatencyMilliseconds);

            _microphone.SetStatus(SourceStatus.Active, _capture.DeviceName);
            _soundsSource.SetStatus(SourceStatus.Active, "Listo");
            _consecutiveOpenFailures = 0;

            TryOpenVirtual();
            PublishStatus();
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
            _microphone.SetStatus(SourceStatus.Waiting, ex.Message);
            _soundsSource.SetStatus(SourceStatus.Waiting, "Esperando a la salida de audio…");
            SetStatus(new EngineStatus(EngineState.Recovering, $"{ex.Message} Reintentando cada {RetryInterval.TotalSeconds:0} s…"));
            return false;
        }
    }

    /// <summary>
    /// Abre la salida del micrófono virtual sin tocar micrófono ni monitoreo. Si falla, el motor sigue y el supervisor
    /// reintenta. Con la salida virtual en marcha, los sonidos se generan a su ritmo para que no dependan de los auriculares.
    /// </summary>
    private void TryOpenVirtual()
    {
        MixBus? virtualBus = _virtualBus;
        EngineOptions? options = _options;
        if (virtualBus is null || options?.VirtualOutputDeviceId is null)
        {
            return;
        }

        int generation = Interlocked.Increment(ref _virtualGeneration);
        Volatile.Write(ref _virtualFailed, 0);
        IAudioStream? stream = null;
        try
        {
            virtualBus.ResetForRestart();
            stream = _renderFactory.Open(
                options.VirtualOutputDeviceId, options.Format, RenderLatencyMilliseconds(options), virtualBus.Render);

            // Si el monitoreo acaba en el mismo dispositivo (p. ej. Windows tiene el cable como salida predeterminada),
            // los demás oirían tu monitoreo: se rechaza en vez de mezclar las dos cosas.
            if (_render is not null && string.Equals(stream.DeviceName, _render.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"El monitoreo está saliendo por «{stream.DeviceName}», el mismo dispositivo que el micrófono virtual. " +
                    "Elige otros auriculares para el monitoreo o cambia la salida predeterminada de Windows.");
            }

            stream.Stopped += ex => OnVirtualStopped(generation, ex);
            virtualBus.SetActive(true);
            SetPrimaryBus(BusKind.Virtual);
            stream.Start();
            _virtualRender = stream;
            _virtualMessage = null;
            _consecutiveVirtualFailures = 0;
            _logger.LogInformation("Micrófono virtual en marcha: «{Device}» ({Latency} ms)", stream.DeviceName, stream.ReportedLatencyMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.Log(
                _consecutiveVirtualFailures++ == 0 ? LogLevel.Warning : LogLevel.Debug,
                ex,
                "No se pudo abrir la salida del micrófono virtual (intento {Attempt})",
                _consecutiveVirtualFailures);
            virtualBus.SetActive(false);
            SetPrimaryBus(BusKind.Monitor);
            DisposeQuietly(ref stream, "micrófono virtual");
            _virtualMessage = $"Micrófono virtual no disponible: {ex.Message}";
        }
    }

    private void CloseVirtual()
    {
        SetPrimaryBus(BusKind.Monitor);
        _virtualBus?.SetActive(false);
        DisposeQuietly(ref _virtualRender, "micrófono virtual");
    }

    private void CloseStreams()
    {
        // Primero la captura (productor) y luego las salidas (consumidores).
        _monitorBus?.SetActive(false);
        DisposeQuietly(ref _capture, "micrófono");
        CloseVirtual();
        DisposeQuietly(ref _render, "salida de monitoreo");
    }

    /// <summary>Decide qué mezcla genera los sonidos. El reproductor protege contra dos hilos generando a la vez.</summary>
    private void SetPrimaryBus(BusKind bus)
    {
        SourceFeed? feed = _soundsSource.Feed;
        if (feed is not null)
        {
            feed.PrimaryBus = bus;
        }
    }

    private void PublishStatus()
    {
        IAudioStream? capture = _capture;
        IAudioStream? render = _render;
        if (capture is null || render is null)
        {
            return;
        }

        IAudioStream? virtualRender = _virtualRender;
        SetStatus(new EngineStatus(
            EngineState.Running,
            "En marcha",
            capture.DeviceName,
            render.DeviceName,
            capture.ReportedLatencyMilliseconds,
            render.ReportedLatencyMilliseconds,
            virtualRender?.DeviceName,
            virtualRender is null ? _virtualMessage : null));
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
        _microphone.SetStatus(SourceStatus.Waiting, $"Se perdió el {role}. Reintentando…");
        SetStatus(new EngineStatus(EngineState.Recovering, $"Se perdió el {role}. Reintentando…"));
        _wake.Release();
    }

    private void OnVirtualStopped(int generation, Exception? exception)
    {
        if (exception is null || generation != Volatile.Read(ref _virtualGeneration))
        {
            return;
        }

        _logger.LogWarning(exception, "La salida del micrófono virtual se detuvo inesperadamente");
        _virtualMessage = "Se perdió la salida del micrófono virtual. Reintentando…";
        Volatile.Write(ref _virtualFailed, 1);
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

    private void RaiseSourcesChanged() => SourcesChanged?.Invoke(this, EventArgs.Empty);
}
