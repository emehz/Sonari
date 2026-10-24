namespace Sonari.Audio.Core.Effects;

/// <summary>Una voz predefinida: aplicarla sustituye toda la cadena.</summary>
public sealed record VoicePreset(string Id, string Name, string Description, VoiceEffectSettings Settings);

public static class VoicePresets
{
    /// <summary>Limpieza básica que comparten casi todas las voces: puerta de ruido, pasaaltos, compresor y limitador.</summary>
    private static VoiceEffectSettings Clean => new()
    {
        NoiseGate = new() { Enabled = true, ThresholdDb = -55 },
        Filter = new() { Enabled = true, HighPassHz = 80 },
        Compressor = new() { Enabled = true, ThresholdDb = -22, Ratio = 3, MakeupDb = 4 },
        Limiter = new() { Enabled = true, CeilingDb = -1 },
    };

    public static IReadOnlyList<VoicePreset> All { get; } =
    [
        new("none", "Sin efectos", "Tu voz tal cual.", new VoiceEffectSettings()),

        new("natural", "Natural", "Tu voz limpia: sin ruido de fondo, sin golpes graves y con volumen estable.", Clean),

        new("robot", "Robot", "Voz metálica de robot.", Clean with
        {
            Filter = new() { Enabled = true, HighPassHz = 150 },
            Modulation = new() { Enabled = true, Mode = ModulationMode.Robot, RobotFrequencyHz = 55, Mix = 1 },
        }),

        new("chipmunk", "Ardilla", "Voz aguda y rápida.", Clean with
        {
            Filter = new() { Enabled = true, HighPassHz = 120 },
            PitchShift = new() { Enabled = true, Semitones = 7 },
        }),

        new("monster", "Monstruo", "Voz grave y áspera.", Clean with
        {
            Equalizer = new() { Enabled = true, LowDb = 4, HighDb = -3 },
            PitchShift = new() { Enabled = true, Semitones = -6 },
            Distortion = new() { Enabled = true, DriveDb = 10, ToneHz = 3000, Mix = 0.4f },
            Reverb = new() { Enabled = true, RoomSize = 0.4f, Damping = 0.6f, Mix = 0.15f },
        }),

        new("radio", "Radio", "Como por una radio o un teléfono viejo.", Clean with
        {
            Filter = new() { Enabled = true, HighPassHz = 400, LowPassHz = 3200 },
            Compressor = new() { Enabled = true, ThresholdDb = -25, Ratio = 6, MakeupDb = 8 },
            Distortion = new() { Enabled = true, DriveDb = 14, ToneHz = 3500, Mix = 0.5f },
        }),

        new("cave", "Cueva", "Una sala enorme con eco.", Clean with
        {
            PitchShift = new() { Enabled = true, Semitones = -1 },
            Echo = new() { Enabled = true, DelayMilliseconds = 220, Feedback = 0.3f, Mix = 0.2f },
            Reverb = new() { Enabled = true, RoomSize = 0.9f, Damping = 0.3f, Mix = 0.5f },
        }),

        new("echo", "Eco", "Repeticiones claras de tu voz.", Clean with
        {
            Echo = new() { Enabled = true, DelayMilliseconds = 380, Feedback = 0.45f, Mix = 0.4f },
        }),
    ];
}
