using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Core.Playback;

/// <summary>
/// Sonido decodificado en memoria, ya en el formato del motor (float32 entrelazado). Se decodifica una vez fuera del
/// hilo de audio; reproducirlo solo copia muestras. Es inmutable y se puede compartir entre reproducciones.
/// </summary>
public sealed class SoundClip
{
    public SoundClip(float[] samples, AudioFormat format, string name)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length % format.Channels != 0)
        {
            throw new ArgumentException("El número de muestras no es múltiplo del número de canales.", nameof(samples));
        }

        Samples = samples;
        Format = format;
        Name = name;
    }

    /// <summary>Muestras entrelazadas. No se deben modificar.</summary>
    public float[] Samples { get; }

    public AudioFormat Format { get; }

    public string Name { get; }

    public int Frames => Samples.Length / Format.Channels;

    public TimeSpan Duration => Format.DurationOf(Frames);

    public long SizeInBytes => Samples.LongLength * sizeof(float);
}

public enum SoundPlaybackStatus
{
    Stopped,
    Playing,
    Paused,
}

public readonly record struct SoundPlaybackState(SoundPlaybackStatus Status, TimeSpan Position, TimeSpan Duration)
{
    public static SoundPlaybackState Stopped { get; } = new(SoundPlaybackStatus.Stopped, TimeSpan.Zero, TimeSpan.Zero);

    public double Progress => Duration > TimeSpan.Zero ? Math.Clamp(Position / Duration, 0, 1) : 0;
}

/// <summary>
/// Reproductor de sonidos del motor. Todas las órdenes son asíncronas y no bloquean: se encolan y el hilo de audio las
/// aplica al principio del siguiente bloque (≈ 10 ms). Un mismo sonido (<c>soundId</c>) tiene como mucho una reproducción
/// activa; volver a reproducirlo la reinicia.
/// </summary>
public interface ISoundPlayer
{
    int MaxVoices { get; }

    /// <summary>Reproducciones activas (sonando o en pausa) ahora mismo.</summary>
    int ActiveVoices { get; }

    /// <summary>
    /// Empieza a reproducir <paramref name="clip"/> desde el principio. Si el clip no está en el formato con el que arrancó
    /// el motor, se ignora (hay que volver a decodificarlo).
    /// </summary>
    void Play(Guid soundId, SoundClip clip, float gainDb, bool loop);

    /// <summary>Detiene con un fundido de pocos milisegundos para evitar el chasquido.</summary>
    void Stop(Guid soundId);

    void Pause(Guid soundId);

    void Resume(Guid soundId);

    void SetGain(Guid soundId, float gainDb);

    void SetLoop(Guid soundId, bool loop);

    /// <summary>Parada de emergencia: corta todos los sonidos (fundido de 5 ms) y descarta órdenes pendientes.</summary>
    void StopAll();

    SoundPlaybackState GetState(Guid soundId);
}

/// <summary>Decodifica archivos de audio al formato del motor. Lento: nunca en el hilo de audio ni en el de UI.</summary>
public interface ISoundDecoder
{
    /// <summary>Extensiones admitidas, en minúsculas y con punto (p. ej. «.mp3»).</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <exception cref="SoundDecodeException">El archivo no existe, no se puede leer o es demasiado largo.</exception>
    SoundClip Decode(string path, AudioFormat target, CancellationToken cancellationToken = default);
}

public sealed class SoundDecodeException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Sonidos listos para reproducir: decodifica en segundo plano la primera vez y después los sirve de memoria.</summary>
public interface ISoundClipProvider
{
    IReadOnlyCollection<string> SupportedExtensions { get; }

    /// <exception cref="SoundDecodeException"/>
    Task<SoundClip> GetAsync(string path, AudioFormat format, CancellationToken cancellationToken = default);

    /// <summary>Libera de memoria un archivo (p. ej. al quitarlo del panel).</summary>
    void Forget(string path);
}
