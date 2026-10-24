namespace Sonari.Audio.Effects;

/// <summary>
/// Un efecto de la cadena de voz. Reglas para todos:
/// <list type="bullet">
/// <item><see cref="Process"/> trabaja en el sitio sobre frames completos y no reserva memoria: se ejecuta en el hilo de
/// captura del micrófono, con prioridad de tiempo real.</item>
/// <item>Los buffers se reservan en el constructor, con el tamaño máximo que pueda necesitar cualquier parámetro.</item>
/// <item><c>Configure</c> (propio de cada efecto) solo cambia parámetros y se llama desde el mismo hilo que
/// <see cref="Process"/>, entre bloques.</item>
/// </list>
/// </summary>
internal abstract class VoiceEffect(int sampleRate, int channels) : IAudioProcessor
{
    protected int SampleRate { get; } = sampleRate;

    protected int Channels { get; } = channels;

    public abstract void Process(Span<float> interleaved);

    /// <summary>Borra el estado interno (colas de eco, envolventes…). Se llama al activar el efecto.</summary>
    public abstract void Reset();

    /// <summary>Coeficiente de un filtro de un polo con la constante de tiempo dada (para envolventes y suavizados).</summary>
    protected float OnePole(double milliseconds) =>
        milliseconds <= 0 ? 1f : (float)(1 - Math.Exp(-1.0 / (SampleRate * milliseconds / 1000)));
}
