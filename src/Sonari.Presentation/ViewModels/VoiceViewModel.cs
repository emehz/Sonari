using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Effects;
using Sonari.Audio.Core.Engine;
using Sonari.Infrastructure.Settings;

namespace Sonari.Presentation.ViewModels;

/// <summary>
/// Efectos de voz: voces predefinidas, interruptor general y una tarjeta por efecto en el orden de la cadena.
/// Cada cambio se aplica al motor al instante (también con el motor en marcha) y se guarda poco después.
/// </summary>
public sealed partial class VoiceViewModel : PageViewModel
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly IAudioEngine _engine;
    private readonly ISettingsService _settings;
    private readonly ILogger<VoiceViewModel> _logger;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<VoiceEffectKind, EffectViewModel> _byKind;
    private bool _loading;

    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>Nombre de la voz predefinida que coincide con la configuración actual, o «Personalizada».</summary>
    [ObservableProperty]
    private string _currentVoiceName = string.Empty;

    public VoiceViewModel(IAudioEngine engine, ISettingsService settings, ILogger<VoiceViewModel> logger)
        : base("voice", "Voz", "\uE720",
            "Efectos en tiempo real para tu micrófono. Los oyen los demás y, si activas el monitoreo del micrófono, tú también.")
    {
        _engine = engine;
        _settings = settings;
        _logger = logger;
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += async (_, _) => await SaveAsync();

        _byKind = CreateEffects().ToDictionary(e => e.Kind);
        Presets = VoicePresets.All;

        // La voz guardada se aplica al arrancar la aplicación, antes de que nadie abra esta página.
        VoiceEffectSettings saved = settings.Current.Voice;
        _engine.VoiceEffects = saved;
        Load(_engine.VoiceEffects);
    }

    public IReadOnlyList<VoicePreset> Presets { get; }

    /// <summary>Los efectos en el orden de la cadena.</summary>
    public ObservableCollection<EffectViewModel> Effects { get; } = [];

    public string MonitorHint =>
        "Para oírte con los efectos, activa «Enviar al monitoreo» en el canal Micrófono del Mezclador (con auriculares, " +
        "para que tus altavoces no se cuelen en el micrófono).";

    [RelayCommand]
    private void ApplyPreset(VoicePreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        _logger.LogInformation("Voz predefinida: {Preset}", preset.Id);
        Load(preset.Settings.Normalize());
        Commit();
    }

    [RelayCommand]
    private void MoveUp(EffectViewModel? effect) => Move(effect, -1);

    [RelayCommand]
    private void MoveDown(EffectViewModel? effect) => Move(effect, +1);

    [RelayCommand]
    private void RestoreOrder()
    {
        Load(Build() with { Order = VoiceEffectSettings.DefaultOrder });
        Commit();
    }

    partial void OnIsEnabledChanged(bool value) => OnEffectChanged();

    private void Move(EffectViewModel? effect, int direction)
    {
        if (effect is null)
        {
            return;
        }

        int index = Effects.IndexOf(effect);
        int target = index + direction;
        if (index < 0 || target < 0 || target >= Effects.Count)
        {
            return;
        }

        Effects.Move(index, target);
        UpdateMoveButtons();
        OnEffectChanged();
    }

    private void OnEffectChanged()
    {
        if (_loading)
        {
            return;
        }

        Commit();
    }

    /// <summary>Envía la configuración de la página al motor y programa el guardado.</summary>
    private void Commit()
    {
        _engine.VoiceEffects = Build();
        UpdateCurrentVoiceName();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        _saveTimer.Stop();
        try
        {
            AppSettings current = _settings.Current;
            VoiceEffectSettings voice = _engine.VoiceEffects;
            if (!Equivalent(current.Voice, voice))
            {
                await _settings.SaveAsync(current with { Voice = voice });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar la configuración de voz");
        }
    }

    private void Load(VoiceEffectSettings settings)
    {
        _loading = true;
        try
        {
            IsEnabled = settings.Enabled;

            Effects.Clear();
            foreach (VoiceEffectKind kind in settings.Order)
            {
                Effects.Add(_byKind[kind]);
            }

            UpdateMoveButtons();

            _byKind[VoiceEffectKind.NoiseGate].Load(settings.NoiseGate.Enabled,
                [("threshold", settings.NoiseGate.ThresholdDb), ("release", settings.NoiseGate.ReleaseMilliseconds)]);
            _byKind[VoiceEffectKind.Filter].Load(settings.Filter.Enabled,
                [("highpass", settings.Filter.HighPassHz), ("lowpass", settings.Filter.LowPassHz)]);
            _byKind[VoiceEffectKind.Equalizer].Load(settings.Equalizer.Enabled,
                [("low", settings.Equalizer.LowDb), ("mid", settings.Equalizer.MidDb), ("high", settings.Equalizer.HighDb)]);
            _byKind[VoiceEffectKind.Compressor].Load(settings.Compressor.Enabled,
            [
                ("threshold", settings.Compressor.ThresholdDb), ("ratio", settings.Compressor.Ratio),
                ("attack", settings.Compressor.AttackMilliseconds), ("release", settings.Compressor.ReleaseMilliseconds),
                ("makeup", settings.Compressor.MakeupDb),
            ]);
            _byKind[VoiceEffectKind.PitchShift].Load(settings.PitchShift.Enabled,
                [("semitones", settings.PitchShift.Semitones)]);
            _byKind[VoiceEffectKind.Distortion].Load(settings.Distortion.Enabled,
                [("drive", settings.Distortion.DriveDb), ("tone", settings.Distortion.ToneHz), ("mix", settings.Distortion.Mix)]);
            _byKind[VoiceEffectKind.Modulation].Load(settings.Modulation.Enabled,
            [
                ("robot", settings.Modulation.RobotFrequencyHz), ("rate", settings.Modulation.RateHz),
                ("depth", settings.Modulation.Depth), ("mix", settings.Modulation.Mix),
            ], settings.Modulation.Mode);
            _byKind[VoiceEffectKind.Echo].Load(settings.Echo.Enabled,
                [("delay", settings.Echo.DelayMilliseconds), ("feedback", settings.Echo.Feedback), ("mix", settings.Echo.Mix)]);
            _byKind[VoiceEffectKind.Reverb].Load(settings.Reverb.Enabled,
                [("room", settings.Reverb.RoomSize), ("damping", settings.Reverb.Damping), ("mix", settings.Reverb.Mix)]);
            _byKind[VoiceEffectKind.Limiter].Load(settings.Limiter.Enabled, [("ceiling", settings.Limiter.CeilingDb)]);
        }
        finally
        {
            _loading = false;
        }

        UpdateCurrentVoiceName();
    }

    private VoiceEffectSettings Build()
    {
        EffectViewModel gate = _byKind[VoiceEffectKind.NoiseGate];
        EffectViewModel filter = _byKind[VoiceEffectKind.Filter];
        EffectViewModel eq = _byKind[VoiceEffectKind.Equalizer];
        EffectViewModel compressor = _byKind[VoiceEffectKind.Compressor];
        EffectViewModel pitch = _byKind[VoiceEffectKind.PitchShift];
        EffectViewModel distortion = _byKind[VoiceEffectKind.Distortion];
        EffectViewModel modulation = _byKind[VoiceEffectKind.Modulation];
        EffectViewModel echo = _byKind[VoiceEffectKind.Echo];
        EffectViewModel reverb = _byKind[VoiceEffectKind.Reverb];
        EffectViewModel limiter = _byKind[VoiceEffectKind.Limiter];

        return new VoiceEffectSettings
        {
            Enabled = IsEnabled,
            Order = Effects.Select(e => e.Kind).ToList(),
            NoiseGate = new() { Enabled = gate.IsEnabled, ThresholdDb = gate["threshold"], ReleaseMilliseconds = gate["release"] },
            Filter = new() { Enabled = filter.IsEnabled, HighPassHz = filter["highpass"], LowPassHz = filter["lowpass"] },
            Equalizer = new() { Enabled = eq.IsEnabled, LowDb = eq["low"], MidDb = eq["mid"], HighDb = eq["high"] },
            Compressor = new()
            {
                Enabled = compressor.IsEnabled,
                ThresholdDb = compressor["threshold"],
                Ratio = compressor["ratio"],
                AttackMilliseconds = compressor["attack"],
                ReleaseMilliseconds = compressor["release"],
                MakeupDb = compressor["makeup"],
            },
            PitchShift = new() { Enabled = pitch.IsEnabled, Semitones = pitch["semitones"] },
            Distortion = new()
            {
                Enabled = distortion.IsEnabled, DriveDb = distortion["drive"], ToneHz = distortion["tone"], Mix = distortion["mix"],
            },
            Modulation = new()
            {
                Enabled = modulation.IsEnabled,
                Mode = modulation.SelectedMode?.Mode ?? ModulationMode.Robot,
                RobotFrequencyHz = modulation["robot"],
                RateHz = modulation["rate"],
                Depth = modulation["depth"],
                Mix = modulation["mix"],
            },
            Echo = new() { Enabled = echo.IsEnabled, DelayMilliseconds = echo["delay"], Feedback = echo["feedback"], Mix = echo["mix"] },
            Reverb = new() { Enabled = reverb.IsEnabled, RoomSize = reverb["room"], Damping = reverb["damping"], Mix = reverb["mix"] },
            Limiter = new() { Enabled = limiter.IsEnabled, CeilingDb = limiter["ceiling"] },
        }.Normalize();
    }

    private void UpdateMoveButtons()
    {
        for (int i = 0; i < Effects.Count; i++)
        {
            Effects[i].CanMoveUp = i > 0;
            Effects[i].CanMoveDown = i < Effects.Count - 1;
        }
    }

    private void UpdateCurrentVoiceName()
    {
        VoiceEffectSettings current = _engine.VoiceEffects;
        VoicePreset? match = Presets.FirstOrDefault(p => Equivalent(p.Settings.Normalize(), current));
        CurrentVoiceName = match?.Name ?? "Personalizada";
    }

    /// <summary>Misma configuración aunque las listas de orden sean objetos distintos.</summary>
    private static bool Equivalent(VoiceEffectSettings a, VoiceEffectSettings b) =>
        a with { Order = b.Order } == b && a.Order.SequenceEqual(b.Order);

    private IEnumerable<EffectViewModel> CreateEffects()
    {
        EffectViewModel Effect(VoiceEffectKind kind, string title, string description) =>
            new(kind, title, description, OnEffectChanged);

        EffectViewModel gate = Effect(VoiceEffectKind.NoiseGate, "Puerta de ruido",
            "Silencia el micrófono cuando no hablas: quita el ventilador, el teclado o el ruido de la calle.");
        gate.Add("threshold", "Umbral", NoiseGateSettings.ThresholdRange, 1, v => $"{v:0} dB");
        gate.Add("release", "Cierre", NoiseGateSettings.ReleaseRange, 5, EffectViewModel.Ms);
        yield return gate;

        EffectViewModel filter = Effect(VoiceEffectKind.Filter, "Filtro",
            "Pasaaltos para quitar golpes y zumbidos graves; pasabajos para una voz más apagada.");
        filter.Add("highpass", "Pasaaltos", FilterSettings.HighPassRange, 5, EffectViewModel.Hz);
        filter.Add("lowpass", "Pasabajos", FilterSettings.LowPassRange, 100,
            v => v >= FilterSettings.LowPassOffHz ? "Apagado" : EffectViewModel.Hz(v));
        yield return filter;

        EffectViewModel eq = Effect(VoiceEffectKind.Equalizer, "Ecualizador",
            $"Graves ({EqualizerSettings.LowHz:0} Hz), medios ({EqualizerSettings.MidHz / 1000:0.0} kHz) y agudos ({EqualizerSettings.HighHz / 1000:0} kHz).");
        eq.Add("low", "Graves", EqualizerSettings.GainRange, 0.5, EffectViewModel.Db);
        eq.Add("mid", "Medios", EqualizerSettings.GainRange, 0.5, EffectViewModel.Db);
        eq.Add("high", "Agudos", EqualizerSettings.GainRange, 0.5, EffectViewModel.Db);
        yield return eq;

        EffectViewModel compressor = Effect(VoiceEffectKind.Compressor, "Compresor",
            "Iguala el volumen: lo que supera el umbral se baja y el conjunto se sube con la ganancia.");
        compressor.Add("threshold", "Umbral", CompressorSettings.ThresholdRange, 1, v => $"{v:0} dB");
        compressor.Add("ratio", "Proporción", CompressorSettings.RatioRange, 0.5, v => $"{v.ToString("0.#", CultureInfo.CurrentCulture)}:1");
        compressor.Add("attack", "Ataque", CompressorSettings.AttackRange, 0.5, EffectViewModel.Ms);
        compressor.Add("release", "Recuperación", CompressorSettings.ReleaseRange, 5, EffectViewModel.Ms);
        compressor.Add("makeup", "Ganancia", CompressorSettings.MakeupRange, 0.5, EffectViewModel.Db);
        yield return compressor;

        EffectViewModel pitch = Effect(VoiceEffectKind.PitchShift, "Tono",
            "Más agudo o más grave sin cambiar la velocidad. Añade unos 20 ms de retardo.");
        pitch.Add("semitones", "Semitonos", PitchShiftSettings.SemitonesRange, 1,
            v => $"{v.ToString("+0;−0;0", CultureInfo.CurrentCulture)} st");
        yield return pitch;

        EffectViewModel distortion = Effect(VoiceEffectKind.Distortion, "Distorsión",
            "Satura la voz: de un poco de cuerpo a un megáfono roto.");
        distortion.Add("drive", "Saturación", DistortionSettings.DriveRange, 1, v => $"{v:0} dB");
        distortion.Add("tone", "Tono", DistortionSettings.ToneRange, 100, EffectViewModel.Hz);
        distortion.Add("mix", "Mezcla", DistortionSettings.MixRange, 0.05, EffectViewModel.Percent);
        yield return distortion;

        EffectViewModel modulation = new(VoiceEffectKind.Modulation, "Modulación",
            "Robot, coro (varias voces) o flanger (barrido metálico).", OnEffectChanged)
        {
            Modes =
            [
                new(ModulationMode.Robot, "Robot"),
                new(ModulationMode.Chorus, "Coro"),
                new(ModulationMode.Flanger, "Flanger"),
            ],
        };
        modulation.Add("robot", "Frecuencia", ModulationSettings.RobotFrequencyRange, 1, EffectViewModel.Hz);
        modulation.Add("rate", "Velocidad", ModulationSettings.RateRange, 0.05, EffectViewModel.Hz);
        modulation.Add("depth", "Profundidad", ModulationSettings.DepthRange, 0.05, EffectViewModel.Percent);
        modulation.Add("mix", "Mezcla", ModulationSettings.MixRange, 0.05, EffectViewModel.Percent);
        yield return modulation;

        EffectViewModel echo = Effect(VoiceEffectKind.Echo, "Eco", "Repeticiones de tu voz que se van apagando.");
        echo.Add("delay", "Tiempo", EchoSettings.DelayRange, 10, EffectViewModel.Ms);
        echo.Add("feedback", "Repeticiones", EchoSettings.FeedbackRange, 0.05, EffectViewModel.Percent);
        echo.Add("mix", "Mezcla", EchoSettings.MixRange, 0.05, EffectViewModel.Percent);
        yield return echo;

        EffectViewModel reverb = Effect(VoiceEffectKind.Reverb, "Reverberación", "De una habitación pequeña a una catedral.");
        reverb.Add("room", "Tamaño", ReverbSettings.RoomSizeRange, 0.05, EffectViewModel.Percent);
        reverb.Add("damping", "Amortiguación", ReverbSettings.DampingRange, 0.05, EffectViewModel.Percent);
        reverb.Add("mix", "Mezcla", ReverbSettings.MixRange, 0.05, EffectViewModel.Percent);
        yield return reverb;

        EffectViewModel limiter = Effect(VoiceEffectKind.Limiter, "Limitador",
            "Ningún pico de tu voz pasa del techo, por mucho que grites.");
        limiter.Add("ceiling", "Techo", LimiterSettings.CeilingRange, 0.5, EffectViewModel.Db);
        yield return limiter;
    }
}
