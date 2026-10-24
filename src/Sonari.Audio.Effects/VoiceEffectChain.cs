using Sonari.Audio.Core.Effects;

namespace Sonari.Audio.Effects;

/// <summary>
/// Cadena de efectos de voz. La crea el motor en cada arranque (los buffers dependen del formato) y la usa un único
/// hilo: el de captura del micrófono.
/// <para>
/// La configuración llega entera con cada bloque. Como es inmutable, basta comparar la referencia para saber si cambió;
/// solo entonces se recalculan los parámetros. Nada en <see cref="Process"/> reserva memoria.
/// </para>
/// </summary>
public sealed class VoiceEffectChain
{
    private readonly int _channels;
    private readonly int _maxBlockSamples;
    private readonly NoiseGate _noiseGate;
    private readonly FilterEffect _filter;
    private readonly Equalizer _equalizer;
    private readonly Compressor _compressor;
    private readonly PitchShifter _pitchShift;
    private readonly Distortion _distortion;
    private readonly Modulation _modulation;
    private readonly Echo _echo;
    private readonly Reverb _reverb;
    private readonly Limiter _limiter;
    private readonly EffectSlot[] _slots;
    private readonly int[] _order;
    private readonly EffectSlot _master;
    private VoiceEffectSettings? _applied;

    public VoiceEffectChain(int sampleRate, int channels, int maxBlockFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlockFrames, 1);
        _channels = channels;
        _maxBlockSamples = maxBlockFrames * channels;

        _noiseGate = new NoiseGate(sampleRate, channels);
        _filter = new FilterEffect(sampleRate, channels);
        _equalizer = new Equalizer(sampleRate, channels);
        _compressor = new Compressor(sampleRate, channels);
        _pitchShift = new PitchShifter(sampleRate, channels);
        _distortion = new Distortion(sampleRate, channels);
        _modulation = new Modulation(sampleRate, channels);
        _echo = new Echo(sampleRate, channels);
        _reverb = new Reverb(sampleRate, channels);
        _limiter = new Limiter(sampleRate, channels);

        // Indexado por VoiceEffectKind.
        VoiceEffect[] effects =
            [_noiseGate, _filter, _equalizer, _compressor, _pitchShift, _distortion, _modulation, _echo, _reverb, _limiter];
        _slots = effects.Select(e => new EffectSlot(e, sampleRate, channels, maxBlockFrames)).ToArray();
        _order = new int[_slots.Length];
        for (int i = 0; i < _order.Length; i++)
        {
            _order[i] = (int)VoiceEffectSettings.DefaultOrder[i];
        }

        _master = new EffectSlot(new SlotSequence(this), sampleRate, channels, maxBlockFrames);
    }

    /// <summary>Procesa en el sitio. Acepta bloques de cualquier tamaño (los trocea internamente).</summary>
    /// <param name="settings">Configuración ya normalizada (<see cref="VoiceEffectSettings.Normalize"/>).</param>
    public void Process(Span<float> interleaved, VoiceEffectSettings settings)
    {
        if (!ReferenceEquals(settings, _applied))
        {
            Apply(settings);
        }

        int whole = interleaved.Length - (interleaved.Length % _channels);
        for (int offset = 0; offset < whole; offset += _maxBlockSamples)
        {
            _master.Process(interleaved.Slice(offset, Math.Min(_maxBlockSamples, whole - offset)));
        }
    }

    private void Apply(VoiceEffectSettings settings)
    {
        bool first = _applied is null;
        _applied = settings;

        // Un efecto que se apaga conserva sus parámetros mientras dura su fundido de salida: si se le aplicaran los
        // nuevos (p. ej. 0 semitonos), su sonido cambiaría de golpe en mitad del fundido y se oiría un clic.
        if (settings.NoiseGate.Enabled)
        {
            _noiseGate.Configure(settings.NoiseGate);
        }

        if (settings.Filter.Enabled)
        {
            _filter.Configure(settings.Filter);
        }

        if (settings.Equalizer.Enabled)
        {
            _equalizer.Configure(settings.Equalizer);
        }

        if (settings.Compressor.Enabled)
        {
            _compressor.Configure(settings.Compressor);
        }

        if (settings.PitchShift.Enabled)
        {
            _pitchShift.Configure(settings.PitchShift);
        }

        if (settings.Distortion.Enabled)
        {
            _distortion.Configure(settings.Distortion);
        }

        if (settings.Modulation.Enabled)
        {
            _modulation.Configure(settings.Modulation);
        }

        if (settings.Echo.Enabled)
        {
            _echo.Configure(settings.Echo);
        }

        if (settings.Reverb.Enabled)
        {
            _reverb.Configure(settings.Reverb);
        }

        if (settings.Limiter.Enabled)
        {
            _limiter.Configure(settings.Limiter);
        }

        for (int kind = 0; kind < _slots.Length; kind++)
        {
            bool enabled = settings.IsEnabled((VoiceEffectKind)kind);
            if (first)
            {
                _slots[kind].SetEnabledImmediately(enabled);
            }
            else
            {
                _slots[kind].SetEnabled(enabled);
            }
        }

        if (settings.Order.Count == _order.Length)
        {
            for (int i = 0; i < _order.Length; i++)
            {
                _order[i] = (int)settings.Order[i];
            }
        }

        if (first)
        {
            _master.SetEnabledImmediately(settings.Enabled);
        }
        else
        {
            _master.SetEnabled(settings.Enabled);
        }
    }

    /// <summary>Los efectos en el orden configurado; el interruptor general lo envuelve con su propio cruce.</summary>
    private sealed class SlotSequence(VoiceEffectChain chain) : IAudioProcessor
    {
        public void Process(Span<float> interleaved)
        {
            int[] order = chain._order;
            for (int i = 0; i < order.Length; i++)
            {
                chain._slots[order[i]].Process(interleaved);
            }
        }

        public void Reset()
        {
            for (int kind = 0; kind < chain._slots.Length; kind++)
            {
                // Volver a encender un efecto desde apagado ya lo reinicia; aquí basta con los que están encendidos.
                if (chain._slots[kind].IsEnabled)
                {
                    chain._slots[kind].ResetProcessor();
                }
            }
        }
    }
}
