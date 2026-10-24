using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Mantiene viva la captura de una fuente de aplicación o de sistema, independientemente del micrófono y de la salida:
/// busca el proceso, abre la captura, vigila que el proceso siga vivo y, si se cierra, espera a que vuelva a abrirse.
/// </summary>
internal sealed class ProcessCaptureRunner
{
    private static readonly TimeSpan WaitForProcessInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProcessAliveInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(3);

    private readonly SourceChannel _source;
    private readonly SourceFeed _feed;
    private readonly AudioFormat _format;
    private readonly IProcessCaptureStreamFactory _factory;
    private readonly IProcessLocator _locator;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;
    private int? _preferredProcessId;
    private int _consecutiveFailures;

    public ProcessCaptureRunner(
        SourceChannel source,
        SourceFeed feed,
        AudioFormat format,
        IProcessCaptureStreamFactory factory,
        IProcessLocator locator,
        ILogger logger)
    {
        _source = source;
        _feed = feed;
        _format = format;
        _factory = factory;
        _locator = locator;
        _logger = logger;
        _preferredProcessId = source.Target?.PreferredProcessId;
    }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    public async Task StopAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Esperado.
        }

        _cts.Dispose();
    }

    private bool IsSystemAudio => _source.Kind == SourceKind.SystemAudio;

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int? processId = IsSystemAudio
                ? _locator.CurrentProcessId
                : _locator.FindRootProcess(_source.ExecutablePath!, _preferredProcessId);

            if (processId is not { } pid)
            {
                _source.SetStatus(SourceStatus.Waiting, "Esperando a que se abra la aplicación…");
                if (!await DelayAsync(WaitForProcessInterval, token).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            bool retryLater = await CaptureAsync(pid, token).ConfigureAwait(false);
            if (retryLater && !await DelayAsync(RetryAfterFailure, token).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <returns><c>true</c> si hay que esperar antes de reintentar (fallo); <c>false</c> si se puede seguir enseguida.</returns>
    private async Task<bool> CaptureAsync(int processId, CancellationToken token)
    {
        IAudioStream? stream = null;
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // En modo sistema se captura todo excepto el árbol de Sonari (evita que el monitoreo vuelva a entrar).
            stream = await _factory.OpenAsync(processId, includeProcessTree: !IsSystemAudio, _format, _feed.OnCaptured, token)
                .ConfigureAwait(false);
            stream.Stopped += ex => stopped.TrySetResult(ex);
            stream.Start();

            _preferredProcessId = processId;
            _consecutiveFailures = 0;
            _source.SetStatus(
                SourceStatus.Active,
                IsSystemAudio ? "Capturando todo el audio de Windows excepto Sonari" : $"Capturando (PID {processId})",
                IsSystemAudio ? null : processId);
            _logger.LogInformation("Captura activa: {Source} PID {ProcessId}", _source, processId);

            while (true)
            {
                Task finished = await Task.WhenAny(stopped.Task, Task.Delay(ProcessAliveInterval, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (finished == stopped.Task)
                {
                    Exception? error = await stopped.Task.ConfigureAwait(false);
                    _logger.LogWarning(error, "La captura de {Source} se detuvo sola", _source);
                    _source.SetStatus(SourceStatus.Faulted, "La captura se interrumpió. Reintentando…");
                    return true;
                }

                if (!IsSystemAudio && !_locator.IsRunning(processId))
                {
                    _logger.LogInformation("{Source}: el proceso {ProcessId} terminó", _source, processId);
                    _source.SetStatus(SourceStatus.Waiting, "La aplicación se cerró. Se reconectará cuando vuelva a abrirse.");
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.Log(
                _consecutiveFailures++ == 0 ? LogLevel.Warning : LogLevel.Debug,
                ex,
                "No se pudo capturar {Source} (PID {ProcessId})",
                _source,
                processId);
            _source.SetStatus(SourceStatus.Faulted, $"No se pudo capturar: {ex.Message} Reintentando…");
            return true;
        }
        finally
        {
            if (stream is not null)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error al cerrar la captura de {Source}", _source);
                }
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
