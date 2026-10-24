namespace Sonari.Audio.Effects;

/// <summary>Procesa audio entrelazado en el sitio y puede volver a su estado inicial: un efecto o una cadena entera.</summary>
internal interface IAudioProcessor
{
    void Process(Span<float> interleaved);

    void Reset();
}
