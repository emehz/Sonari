using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sonari.Audio.Core.Endpoints;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Devices;

namespace Sonari.Audio.Capture;

/// <summary>
/// Abre micrófonos en modo compartido, por eventos y con prioridad MMCSS «Pro Audio». Se pide directamente el formato
/// del motor (float32 a la frecuencia y canales configurados): el motor de audio de Windows convierte frecuencia,
/// canales y profundidad (AUTOCONVERTPCM), comprobado en la Fase 2 con un micro USB y con un auricular 44,1 kHz/8 canales.
/// </summary>
public sealed class WasapiCaptureStreamFactory(ILoggerFactory loggerFactory) : ICaptureStreamFactory
{
    public IAudioStream Open(string? deviceId, AudioFormat format, int bufferMilliseconds, CaptureDataHandler onData)
    {
        ArgumentNullException.ThrowIfNull(onData);

        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        WasapiRecorder? recorder = null;
        try
        {
            device = WasapiEndpointResolver.Resolve(enumerator, deviceId, DataFlow.Capture);
            recorder = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels))
                .WithBufferLength(bufferMilliseconds)
                .WithMmcssThreadPriority("Pro Audio")
                .Build();

            return new WasapiRecorderStream(
                recorder, format, device.FriendlyName, onData, loggerFactory.CreateLogger<WasapiRecorderStream>(),
                ownedResources: [device, enumerator]);
        }
        catch
        {
            recorder?.Dispose();
            device?.Dispose();
            enumerator.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Adapta un <see cref="WasapiRecorder"/> ya configurado (micrófono o loopback de proceso) a <see cref="IAudioStream"/>:
/// comprueba el formato, traduce las marcas de WASAPI y garantiza un único aviso de parada.
/// </summary>
internal sealed class WasapiRecorderStream : IAudioStream
{
    private readonly ILogger _logger;
    private readonly CaptureDataHandler _onData;
    private readonly WasapiRecorder _recorder;
    private readonly IDisposable[] _ownedResources;
    private readonly float[] _silence;
    private int _stoppedRaised;

    public WasapiRecorderStream(
        WasapiRecorder recorder,
        AudioFormat format,
        string deviceName,
        CaptureDataHandler onData,
        ILogger logger,
        IDisposable[]? ownedResources = null)
    {
        _recorder = recorder;
        _onData = onData;
        _logger = logger;
        _ownedResources = ownedResources ?? [];
        Format = format;
        DeviceName = deviceName;
        _silence = new float[format.SampleRate * format.Channels / 10];

        WaveFormat actual = recorder.WaveFormat;
        bool isFloat = actual.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible;
        if (!isFloat || actual.BitsPerSample != 32 || actual.SampleRate != format.SampleRate || actual.Channels != format.Channels)
        {
            // El llamador libera el recorder y los recursos al recibir la excepción.
            throw new NotSupportedException($"La captura entregó un formato inesperado ({actual}).");
        }

        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public AudioFormat Format { get; }

    public string DeviceName { get; }

    public double ReportedLatencyMilliseconds => _recorder.LatencyMilliseconds;

    public event Action<Exception?>? Stopped;

    public void Start() => _recorder.StartRecording();

    public void Stop() => _recorder.StopRecording();

    public void Dispose()
    {
        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.Dispose();
        foreach (IDisposable resource in _ownedResources)
        {
            resource.Dispose();
        }
    }

    // Hilo de captura de NAudio.
    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        bool discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(buffer);

        if ((flags & AudioClientBufferFlags.Silent) == 0)
        {
            _onData(samples, discontinuity);
            return;
        }

        // Con la marca «silencio» el contenido del buffer no es fiable: se entrega silencio de la misma duración.
        int remaining = samples.Length;
        while (remaining > 0)
        {
            int length = Math.Min(remaining, _silence.Length);
            _onData(_silence.AsSpan(0, length), discontinuity);
            discontinuity = false;
            remaining -= length;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (Interlocked.Exchange(ref _stoppedRaised, 1) != 0)
        {
            return;
        }

        if (e.Exception is not null)
        {
            _logger.LogDebug(e.Exception, "La captura de «{Device}» terminó con error", DeviceName);
        }

        Stopped?.Invoke(e.Exception);
    }
}
