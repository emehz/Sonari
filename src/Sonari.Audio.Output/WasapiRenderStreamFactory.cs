using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Devices;

namespace Sonari.Audio.Output;

/// <summary>
/// Abre salidas en modo compartido, por eventos y con MMCSS «Pro Audio». El motor entrega float32 en su formato
/// interno y Windows lo adapta al formato de mezcla del dispositivo.
/// </summary>
public sealed class WasapiRenderStreamFactory(ILoggerFactory loggerFactory) : IRenderStreamFactory
{
    public IAudioStream Open(string? deviceId, AudioFormat format, int latencyMilliseconds, RenderHandler render)
    {
        ArgumentNullException.ThrowIfNull(render);
        return new WasapiRenderStream(deviceId, format, latencyMilliseconds, render,
            loggerFactory.CreateLogger<WasapiRenderStream>());
    }
}

internal sealed class WasapiRenderStream : IAudioStream
{
    private readonly ILogger _logger;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly WasapiPlayer _player;
    private int _stoppedRaised;

    public WasapiRenderStream(string? deviceId, AudioFormat format, int latencyMilliseconds, RenderHandler render, ILogger logger)
    {
        _logger = logger;
        Format = format;

        _enumerator = new MMDeviceEnumerator();
        try
        {
            _device = WasapiEndpointResolver.Resolve(_enumerator, deviceId, DataFlow.Render);
            DeviceName = _device.FriendlyName;

            _player = new WasapiPlayerBuilder()
                .WithDevice(_device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(latencyMilliseconds)
                .WithMmcssThreadPriority("Pro Audio")
                .Build();
            _player.Init(new CallbackWaveProvider(format, render, logger));
        }
        catch
        {
            _player?.Dispose();
            _device?.Dispose();
            _enumerator.Dispose();
            throw;
        }

        _player.PlaybackStopped += OnPlaybackStopped;
    }

    public AudioFormat Format { get; }

    public string DeviceName { get; }

    public double ReportedLatencyMilliseconds => _player.LatencyMilliseconds;

    public event Action<Exception?>? Stopped;

    public void Start() => _player.Play();

    public void Stop() => _player.Stop();

    public void Dispose()
    {
        _player?.Dispose();
        _device?.Dispose();
        _enumerator.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) != 0)
        {
            return;
        }

        if (e.Exception is not null)
        {
            _logger.LogDebug(e.Exception, "La salida «{Device}» terminó con error", DeviceName);
        }

        Stopped?.Invoke(e.Exception);
    }

    /// <summary>Adaptador que NAudio llama desde su hilo de render para pedir el siguiente bloque.</summary>
    private sealed class CallbackWaveProvider(AudioFormat format, RenderHandler render, ILogger logger) : IWaveProvider
    {
        private int _faultLogged;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

        public int Read(Span<byte> buffer)
        {
            Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer);
            try
            {
                render(samples);
            }
            catch (Exception ex)
            {
                // Un fallo de programación en el grafo no debe matar el hilo de audio: se emite silencio y se registra una vez.
                samples.Clear();
                if (Interlocked.Exchange(ref _faultLogged, 1) == 0)
                {
                    logger.LogError(ex, "Excepción en el hilo de render; se emite silencio");
                }
            }

            // Devolver menos bytes significaría «fin del audio» para NAudio.
            return buffer.Length;
        }
    }
}
