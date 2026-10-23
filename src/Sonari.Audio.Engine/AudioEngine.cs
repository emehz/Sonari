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
/// Motor de audio: micrófono y fuentes de aplicación/sistema → compensación de deriva → bus de monitoreo → auriculares.
/// <para>
/// Un supervisor abre el micrófono y la salida y los vuelve a abrir si fallan, desaparecen o cambia el predeterminado
/// de Windows. Cada fuente de aplicación tiene su propio bucle de captura (<see cref="ProcessCaptureRunner"/>), de modo
/// que añadir, quitar o perder una aplicación no interrumpe al resto. Los controles de las fuentes viven fuera de los
/// arranques, así que ganancias y envíos se conservan.
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
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    // Protege _sources, _runners, _monitorBus y _running frente a añadir/quitar fuentes desde la UI durante un arranque.
    private readonly object _sourcesLock = new();
    private readonly Dictionary<Guid, ProcessCaptureRunner> _runners = [];
    private SourceChannel[] _sources;
    private bool _running;

    private EngineStatus _status = EngineStatus.Stopped;
    private EngineOptions? _options;
    private int _targetFrames;
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
                "Arrancando motor: {Format}, bloque {Block} ms, entrada {Input}, monitoreo {Monitor}",
                options.Format, options.BlockMilliseconds, options.InputDeviceId ?? "(predeterminada)",
                options.MonitorDeviceId ?? "(predeterminada)");

            lock (_sourcesLock)
            {
                Volatile.Write(ref _options, options);
                _targetFrames = options.Format.FramesFor(TimeSpan.FromMilliseconds(TargetBufferMilliseconds(options)));
                _monitorBus = new MonitorBus(_monitor, options.Format, () => IsGlobalMuted);
                _soundPlayer.Configure(options.Format);
                foreach (SourceChannel source in _sources)
                {
                    source.Mixer = CreateMixer(source);
                }

                PublishBusChannels();
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
                PublishBusChannels();
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
        MonitorBus? bus = Volatile.Read(ref _monitorBus);
        return bus is null
            ? default
            : new EngineMetrics(
                bus.OutputMeter.ReadAndReset(), bus.Limiter.ReadAndResetGainReductionDb(), bus.ClippedSamples, bus.Resyncs);
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

    private void AddSource(SourceChannel source)
    {
        lock (_sourcesLock)
        {
            Volatile.Write(ref _sources, [.. _sources, source]);
            if (_running)
            {
                source.Mixer = CreateMixer(source);
                PublishBusChannels();
                StartRunner(source);
            }
        }

        _logger.LogInformation("Fuente añadida: {Source}", source);
        RaiseSourcesChanged();
    }

    private static bool IsProcessSource(SourceKind kind) => kind is SourceKind.Application or SourceKind.SystemAudio;

    private ChannelStrip CreateMixer(SourceChannel source) => source.Kind == SourceKind.SoundPlayer
        ? new GeneratorChannel(source.Control, _options!.Format.Channels, _soundPlayer.Render)
        : new MixerChannel(source.Control, _options!.Format, _targetFrames, isIntermittent: source.Kind != SourceKind.Microphone);

    /// <summary>Con <see cref="_sourcesLock"/> tomado.</summary>
    private void PublishBusChannels() =>
        _monitorBus?.SetChannels(_sources.Select(s => s.Mixer).OfType<ChannelStrip>().ToArray());

    /// <summary>Con <see cref="_sourcesLock"/> tomado y el motor en marcha.</summary>
    private void StartRunner(SourceChannel source)
    {
        var runner = new ProcessCaptureRunner(
            source, (MixerChannel)source.Mixer!, _options!.Format, _processCaptureFactory, _processLocator, _logger);
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

        source.Mixer = null;
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
                source.Mixer = null;
            }

            // Con el render ya parado: lo que sonaba se descarta para que la UI no muestre reproducciones fantasma.
            _soundPlayer.Configure(_options?.Format ?? AudioFormat.Default);
            Volatile.Write(ref _monitorBus, null);
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
        MixerChannel micMixer = (MixerChannel)_microphone.Mixer!;
        int generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _streamFailed, 0);
        bus.ResetForRestart();

        try
        {
            _render = _renderFactory.Open(
                options.MonitorDeviceId, options.Format, RenderLatencyMilliseconds(options), bus.Render);
            _render.Stopped += ex => OnStreamStopped(generation, "salida de monitoreo", ex);

            _capture = _captureFactory.Open(
                options.InputDeviceId, options.Format, CaptureBufferMilliseconds(options), micMixer.OnCaptured);
            _capture.Stopped += ex => OnStreamStopped(generation, "micrófono", ex);

            _render.Start();
            _capture.Start();

            _logger.LogInformation(
                "Motor en marcha: entrada «{Input}» (buffer {InLatency} ms), salida «{Output}» ({OutLatency} ms)",
                _capture.DeviceName, _capture.ReportedLatencyMilliseconds,
                _render.DeviceName, _render.ReportedLatencyMilliseconds);

            _microphone.SetStatus(SourceStatus.Active, _capture.DeviceName);
            _soundsSource.SetStatus(SourceStatus.Active, "Listo");
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
            _microphone.SetStatus(SourceStatus.Waiting, ex.Message);
            _soundsSource.SetStatus(SourceStatus.Waiting, "Esperando a la salida de audio…");
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
        _microphone.SetStatus(SourceStatus.Waiting, $"Se perdió el {role}. Reintentando…");
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

    private void RaiseSourcesChanged() => SourcesChanged?.Invoke(this, EventArgs.Empty);
}
