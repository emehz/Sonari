using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OndaMix.Audio.Core.Endpoints;
using OndaMix.Audio.Core.Formats;
using OndaMix.Audio.Devices;

namespace OndaMix.Audio.Capture;

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
        return new WasapiCaptureStream(deviceId, format, bufferMilliseconds, onData,
            loggerFactory.CreateLogger<WasapiCaptureStream>());
    }
}

internal sealed class WasapiCaptureStream : IAudioStream
{
    private readonly ILogger _logger;
    private readonly CaptureDataHandler _onData;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private readonly float[] _silence;
    private int _stoppedRaised;

    public WasapiCaptureStream(
        string? deviceId, AudioFormat format, int bufferMilliseconds, CaptureDataHandler onData, ILogger logger)
    {
        _logger = logger;
        _onData = onData;
        Format = format;
        _silence = new float[format.SampleRate * format.Channels / 10];

        _enumerator = new MMDeviceEnumerator();
        try
        {
            _device = WasapiEndpointResolver.Resolve(_enumerator, deviceId, DataFlow.Capture);
            DeviceName = _device.FriendlyName;

            _recorder = new WasapiRecorderBuilder()
                .WithDevice(_device)
                .WithSharedMode()
                .WithEventSync()
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels))
                .WithBufferLength(bufferMilliseconds)
                .WithMmcssThreadPriority("Pro Audio")
                .Build();
        }
        catch
        {
            _device?.Dispose();
            _enumerator.Dispose();
            throw;
        }

        WaveFormat actual = _recorder.WaveFormat;
        bool isFloat = actual.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible;
        if (!isFloat || actual.BitsPerSample != 32 || actual.SampleRate != format.SampleRate || actual.Channels != format.Channels)
        {
            Dispose();
            throw new NotSupportedException($"El micrófono entregó un formato inesperado ({actual}).");
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
        if (_recorder is not null)
        {
            _recorder.DataAvailable -= OnDataAvailable;
            _recorder.Dispose();
        }

        _device?.Dispose();
        _enumerator.Dispose();
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
