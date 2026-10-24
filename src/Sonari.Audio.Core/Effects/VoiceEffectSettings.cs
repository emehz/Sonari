namespace Sonari.Audio.Core.Effects;

/// <summary>Efectos de la cadena de voz. El orden por defecto es el de <see cref="VoiceEffectSettings.DefaultOrder"/>.</summary>
public enum VoiceEffectKind
{
    NoiseGate,
    Filter,
    Equalizer,
    Compressor,
    PitchShift,
    Distortion,
    Modulation,
    Echo,
    Reverb,
    Limiter,
}

public enum ModulationMode
{
    /// <summary>Modulación en anillo: voz metálica de robot.</summary>
    Robot,

    /// <summary>Varias voces ligeramente desafinadas y retrasadas.</summary>
    Chorus,

    /// <summary>Barrido metálico (retardo muy corto con realimentación).</summary>
    Flanger,
}

/// <summary>Rango válido de un parámetro. La UI lo usa para los deslizadores y <c>Normalize</c> para corregir valores.</summary>
public readonly record struct ParameterRange(float Min, float Max)
{
    public float Clamp(float value) => float.IsNaN(value) ? Min : Math.Clamp(value, Min, Max);
}

/// <summary>Silencia la entrada cuando baja de un umbral: elimina el ruido de fondo entre frases.</summary>
public sealed record NoiseGateSettings
{
    public static readonly ParameterRange ThresholdRange = new(-80, -10);
    public static readonly ParameterRange ReleaseRange = new(20, 500);

    public bool Enabled { get; init; }

    public float ThresholdDb { get; init; } = -50;

    public float ReleaseMilliseconds { get; init; } = 120;

    public NoiseGateSettings Normalize() => this with
    {
        ThresholdDb = ThresholdRange.Clamp(ThresholdDb),
        ReleaseMilliseconds = ReleaseRange.Clamp(ReleaseMilliseconds),
    };
}

/// <summary>Pasaaltos (quita graves y golpes) y pasabajos (quita agudos). El pasabajos en su máximo no actúa.</summary>
public sealed record FilterSettings
{
    public static readonly ParameterRange HighPassRange = new(20, 500);
    public static readonly ParameterRange LowPassRange = new(1000, 20000);

    /// <summary>A partir de esta frecuencia el pasabajos se considera desactivado.</summary>
    public const float LowPassOffHz = 19000;

    public bool Enabled { get; init; }

    public float HighPassHz { get; init; } = 80;

    public float LowPassHz { get; init; } = 20000;

    public FilterSettings Normalize() => this with
    {
        HighPassHz = HighPassRange.Clamp(HighPassHz),
        LowPassHz = LowPassRange.Clamp(LowPassHz),
    };
}

/// <summary>Ecualizador de tres bandas: graves (estante en 150 Hz), medios (campana en 1,5 kHz) y agudos (estante en 6 kHz).</summary>
public sealed record EqualizerSettings
{
    public static readonly ParameterRange GainRange = new(-12, 12);

    public const float LowHz = 150;
    public const float MidHz = 1500;
    public const float HighHz = 6000;

    public bool Enabled { get; init; }

    public float LowDb { get; init; }

    public float MidDb { get; init; }

    public float HighDb { get; init; }

    public EqualizerSettings Normalize() => this with
    {
        LowDb = GainRange.Clamp(LowDb),
        MidDb = GainRange.Clamp(MidDb),
        HighDb = GainRange.Clamp(HighDb),
    };
}

/// <summary>Iguala el volumen de la voz: baja lo que supera el umbral y sube el conjunto con la ganancia de compensación.</summary>
public sealed record CompressorSettings
{
    public static readonly ParameterRange ThresholdRange = new(-50, 0);
    public static readonly ParameterRange RatioRange = new(1, 20);
    public static readonly ParameterRange AttackRange = new(0.5f, 50);
    public static readonly ParameterRange ReleaseRange = new(20, 500);
    public static readonly ParameterRange MakeupRange = new(0, 24);

    public bool Enabled { get; init; }

    public float ThresholdDb { get; init; } = -20;

    public float Ratio { get; init; } = 4;

    public float AttackMilliseconds { get; init; } = 5;

    public float ReleaseMilliseconds { get; init; } = 120;

    public float MakeupDb { get; init; } = 6;

    public CompressorSettings Normalize() => this with
    {
        ThresholdDb = ThresholdRange.Clamp(ThresholdDb),
        Ratio = RatioRange.Clamp(Ratio),
        AttackMilliseconds = AttackRange.Clamp(AttackMilliseconds),
        ReleaseMilliseconds = ReleaseRange.Clamp(ReleaseMilliseconds),
        MakeupDb = MakeupRange.Clamp(MakeupDb),
    };
}

/// <summary>Sube o baja el tono sin cambiar la velocidad. No conserva los formantes: de ahí la voz de ardilla o de monstruo.</summary>
public sealed record PitchShiftSettings
{
    public static readonly ParameterRange SemitonesRange = new(-12, 12);

    public bool Enabled { get; init; }

    public float Semitones { get; init; }

    public PitchShiftSettings Normalize() => this with { Semitones = SemitonesRange.Clamp(Semitones) };
}

/// <summary>Saturación suave (tanh) con control de tono. El nivel se compensa para que no suba con la saturación.</summary>
public sealed record DistortionSettings
{
    public static readonly ParameterRange DriveRange = new(0, 40);
    public static readonly ParameterRange ToneRange = new(1000, 12000);
    public static readonly ParameterRange MixRange = new(0, 1);

    public bool Enabled { get; init; }

    public float DriveDb { get; init; } = 18;

    public float ToneHz { get; init; } = 5000;

    public float Mix { get; init; } = 1;

    public DistortionSettings Normalize() => this with
    {
        DriveDb = DriveRange.Clamp(DriveDb),
        ToneHz = ToneRange.Clamp(ToneHz),
        Mix = MixRange.Clamp(Mix),
    };
}

/// <summary>Robot (modulación en anillo), coro o flanger.</summary>
public sealed record ModulationSettings
{
    public static readonly ParameterRange RobotFrequencyRange = new(20, 200);
    public static readonly ParameterRange RateRange = new(0.05f, 5);
    public static readonly ParameterRange DepthRange = new(0, 1);
    public static readonly ParameterRange MixRange = new(0, 1);

    public bool Enabled { get; init; }

    public ModulationMode Mode { get; init; } = ModulationMode.Robot;

    /// <summary>Frecuencia de la portadora del robot. Solo para <see cref="ModulationMode.Robot"/>.</summary>
    public float RobotFrequencyHz { get; init; } = 60;

    /// <summary>Velocidad del barrido. Solo para coro y flanger.</summary>
    public float RateHz { get; init; } = 0.8f;

    /// <summary>Profundidad del barrido. Solo para coro y flanger.</summary>
    public float Depth { get; init; } = 0.5f;

    public float Mix { get; init; } = 0.5f;

    public ModulationSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : ModulationMode.Robot,
        RobotFrequencyHz = RobotFrequencyRange.Clamp(RobotFrequencyHz),
        RateHz = RateRange.Clamp(RateHz),
        Depth = DepthRange.Clamp(Depth),
        Mix = MixRange.Clamp(Mix),
    };
}

/// <summary>Repeticiones de la voz. Las repeticiones se suman a la voz original, que no pierde nivel.</summary>
public sealed record EchoSettings
{
    public static readonly ParameterRange DelayRange = new(20, 1000);
    public static readonly ParameterRange FeedbackRange = new(0, 0.9f);
    public static readonly ParameterRange MixRange = new(0, 1);

    public bool Enabled { get; init; }

    public float DelayMilliseconds { get; init; } = 300;

    public float Feedback { get; init; } = 0.35f;

    public float Mix { get; init; } = 0.3f;

    public EchoSettings Normalize() => this with
    {
        DelayMilliseconds = DelayRange.Clamp(DelayMilliseconds),
        Feedback = FeedbackRange.Clamp(Feedback),
        Mix = MixRange.Clamp(Mix),
    };
}

/// <summary>Reverberación (sala, cueva, catedral). Se suma a la voz original.</summary>
public sealed record ReverbSettings
{
    public static readonly ParameterRange RoomSizeRange = new(0, 1);
    public static readonly ParameterRange DampingRange = new(0, 1);
    public static readonly ParameterRange MixRange = new(0, 1);

    public bool Enabled { get; init; }

    public float RoomSize { get; init; } = 0.6f;

    public float Damping { get; init; } = 0.5f;

    public float Mix { get; init; } = 0.25f;

    public ReverbSettings Normalize() => this with
    {
        RoomSize = RoomSizeRange.Clamp(RoomSize),
        Damping = DampingRange.Clamp(Damping),
        Mix = MixRange.Clamp(Mix),
    };
}

/// <summary>Último eslabón: ningún pico de la voz supera el techo.</summary>
public sealed record LimiterSettings
{
    public static readonly ParameterRange CeilingRange = new(-24, 0);

    public bool Enabled { get; init; }

    public float CeilingDb { get; init; } = -1;

    public LimiterSettings Normalize() => this with { CeilingDb = CeilingRange.Clamp(CeilingDb) };
}

/// <summary>
/// Configuración completa de la cadena de voz. Es inmutable: para cambiar algo se crea una copia con <c>with</c> y se
/// entrega entera al motor, que la aplica en el siguiente bloque de audio.
/// </summary>
public sealed record VoiceEffectSettings
{
    public static readonly IReadOnlyList<VoiceEffectKind> DefaultOrder =
    [
        VoiceEffectKind.NoiseGate,
        VoiceEffectKind.Filter,
        VoiceEffectKind.Equalizer,
        VoiceEffectKind.Compressor,
        VoiceEffectKind.PitchShift,
        VoiceEffectKind.Distortion,
        VoiceEffectKind.Modulation,
        VoiceEffectKind.Echo,
        VoiceEffectKind.Reverb,
        VoiceEffectKind.Limiter,
    ];

    /// <summary>Interruptor general. Apagado, la voz pasa sin tocar aunque haya efectos activados.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Orden de la cadena: cada efecto aparece una vez.</summary>
    public IReadOnlyList<VoiceEffectKind> Order { get; init; } = DefaultOrder;

    public NoiseGateSettings NoiseGate { get; init; } = new();

    public FilterSettings Filter { get; init; } = new();

    public EqualizerSettings Equalizer { get; init; } = new();

    public CompressorSettings Compressor { get; init; } = new();

    public PitchShiftSettings PitchShift { get; init; } = new();

    public DistortionSettings Distortion { get; init; } = new();

    public ModulationSettings Modulation { get; init; } = new();

    public EchoSettings Echo { get; init; } = new();

    public ReverbSettings Reverb { get; init; } = new();

    public LimiterSettings Limiter { get; init; } = new();

    public bool IsEnabled(VoiceEffectKind kind) => kind switch
    {
        VoiceEffectKind.NoiseGate => NoiseGate.Enabled,
        VoiceEffectKind.Filter => Filter.Enabled,
        VoiceEffectKind.Equalizer => Equalizer.Enabled,
        VoiceEffectKind.Compressor => Compressor.Enabled,
        VoiceEffectKind.PitchShift => PitchShift.Enabled,
        VoiceEffectKind.Distortion => Distortion.Enabled,
        VoiceEffectKind.Modulation => Modulation.Enabled,
        VoiceEffectKind.Echo => Echo.Enabled,
        VoiceEffectKind.Reverb => Reverb.Enabled,
        VoiceEffectKind.Limiter => Limiter.Enabled,
        _ => false,
    };

    /// <summary>Corrige valores fuera de rango y órdenes incompletos o repetidos (p. ej. tras editar el JSON a mano).</summary>
    public VoiceEffectSettings Normalize() => this with
    {
        Order = NormalizeOrder(Order),
        NoiseGate = (NoiseGate ?? new()).Normalize(),
        Filter = (Filter ?? new()).Normalize(),
        Equalizer = (Equalizer ?? new()).Normalize(),
        Compressor = (Compressor ?? new()).Normalize(),
        PitchShift = (PitchShift ?? new()).Normalize(),
        Distortion = (Distortion ?? new()).Normalize(),
        Modulation = (Modulation ?? new()).Normalize(),
        Echo = (Echo ?? new()).Normalize(),
        Reverb = (Reverb ?? new()).Normalize(),
        Limiter = (Limiter ?? new()).Normalize(),
    };

    private static IReadOnlyList<VoiceEffectKind> NormalizeOrder(IReadOnlyList<VoiceEffectKind>? order)
    {
        var result = new List<VoiceEffectKind>(DefaultOrder.Count);
        foreach (VoiceEffectKind kind in order ?? [])
        {
            if (Enum.IsDefined(kind) && !result.Contains(kind))
            {
                result.Add(kind);
            }
        }

        // Los que falten van al final, en su posición por defecto relativa.
        result.AddRange(DefaultOrder.Where(k => !result.Contains(k)));
        return result;
    }
}
