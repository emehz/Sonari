using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;

namespace Sonari.Audio.Playback;

/// <summary>
/// Decodifica con Media Foundation (los códecs de Windows: WAV, MP3, AAC/M4A, WMA y FLAC en Windows 10+) y convierte
/// al formato del motor: canales (mono → estéreo, multicanal → los dos primeros) y frecuencia (remuestreador WDL).
/// Todo el sonido queda en memoria, así que hay un límite de duración.
/// </summary>
public sealed class MediaFoundationSoundDecoder : ISoundDecoder
{
    /// <summary>5 minutos en estéreo a 48 kHz son ≈ 110 MB en float32: suficiente para efectos y música de fondo.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);

    private static readonly Lazy<bool> MediaFoundationStarted = new(() =>
    {
        MediaFoundationApi.Startup();
        return true;
    });

    public IReadOnlyCollection<string> SupportedExtensions { get; } =
        [".wav", ".mp3", ".m4a", ".aac", ".wma", ".flac"];

    public SoundClip Decode(string path, AudioFormat target, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new SoundDecodeException($"No se encuentra el archivo «{Path.GetFileName(path)}».");
        }

        _ = MediaFoundationStarted.Value;
        try
        {
            using var reader = new MediaFoundationReader(path);
            if (reader.TotalTime > MaxDuration)
            {
                throw new SoundDecodeException(
                    $"«{Path.GetFileName(path)}» dura {reader.TotalTime:m\\:ss}; el máximo es {MaxDuration.TotalMinutes:0} minutos.");
            }

            ISampleProvider provider = reader.ToSampleProvider();
            if (provider.WaveFormat.Channels == 1 && target.Channels == 2)
            {
                provider = new MonoToStereoSampleProvider(provider);
            }

            if (provider.WaveFormat.SampleRate != target.SampleRate)
            {
                provider = new WdlResamplingSampleProvider(provider, target.SampleRate);
            }

            float[] samples = ReadAll(provider, reader.TotalTime, target, cancellationToken);
            return new SoundClip(samples, target, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is COMException or InvalidDataException or ArgumentException or IOException)
        {
            throw new SoundDecodeException($"No se pudo leer «{Path.GetFileName(path)}»: {ex.Message}", ex);
        }
    }

    private static float[] ReadAll(ISampleProvider provider, TimeSpan expected, AudioFormat target, CancellationToken token)
    {
        int sourceChannels = provider.WaveFormat.Channels;
        int estimatedFrames = (int)Math.Min(target.SampleRate * (expected.TotalSeconds + 1), int.MaxValue / 4);
        var output = new List<float>(estimatedFrames * target.Channels);
        var buffer = new float[target.SampleRate / 10 * sourceChannels];

        int read;
        while ((read = provider.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            int frames = read / sourceChannels;
            for (int frame = 0; frame < frames; frame++)
            {
                int index = frame * sourceChannels;
                for (int c = 0; c < target.Channels; c++)
                {
                    // Multicanal → estéreo: se quedan los dos primeros canales (frontal izquierdo y derecho).
                    output.Add(buffer[index + Math.Min(c, sourceChannels - 1)]);
                }
            }
        }

        return output.ToArray();
    }
}
