using Sonari.Audio.Core.Effects;
using Sonari.Audio.Effects;

namespace Sonari.Audio.Engine.Routing;

/// <summary>Configuración de la cadena de voz escrita por la UI y leída por el hilo de captura una vez por bloque.</summary>
internal sealed class VoiceEffectsControl
{
    private VoiceEffectSettings _settings = new VoiceEffectSettings().Normalize();

    public VoiceEffectSettings Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, (value ?? new VoiceEffectSettings()).Normalize());
    }
}

/// <summary>La cadena de voz de una sesión del motor junto con la configuración que debe aplicar.</summary>
internal sealed class VoiceProcessor(VoiceEffectChain chain, VoiceEffectsControl control)
{
    public void Process(Span<float> interleaved) => chain.Process(interleaved, control.Settings);
}
