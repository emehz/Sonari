using Sonari.Audio.Core.Effects;
using Sonari.Audio.Effects;
using static Sonari.UnitTests.Effects.Signals;

namespace Sonari.UnitTests.Effects;

public class FilterAndEqualizerTests
{
    private static double GainDb(VoiceEffect effect, double frequency)
    {
        float[] output = Run(effect, Sine(frequency, 0.5, 1.2));
        return Db(Amplitude(output, frequency, 0.2, 1.0) / 0.5);
    }

    [Fact]
    public void HighPass_RemovesLowsAndKeepsTheVoice()
    {
        var filter = new FilterEffect(Rate, Channels);
        filter.Configure(new FilterSettings { Enabled = true, HighPassHz = 200 });

        Assert.True(GainDb(filter, 50) < -20);
        Assert.Equal(0, GainDb(filter, 2000), 0.5);
    }

    [Fact]
    public void LowPass_RemovesHighs()
    {
        var filter = new FilterEffect(Rate, Channels);
        filter.Configure(new FilterSettings { Enabled = true, HighPassHz = 20, LowPassHz = 1000 });

        Assert.True(GainDb(filter, 8000) < -30);
        Assert.Equal(0, GainDb(filter, 200), 0.5);
    }

    [Theory]
    [InlineData(40, 6, 0, 0)]
    [InlineData(1500, 0, 6, 0)]
    [InlineData(16000, 0, 0, 6)]
    [InlineData(1500, 0, -9, 0)]
    public void Equalizer_BoostsAndCutsEachBand(double frequency, float low, float mid, float high)
    {
        var eq = new Equalizer(Rate, Channels);
        eq.Configure(new EqualizerSettings { Enabled = true, LowDb = low, MidDb = mid, HighDb = high });

        Assert.Equal(low + mid + high, GainDb(eq, frequency), 0.7);
    }

    [Fact]
    public void Equalizer_Flat_IsTransparent()
    {
        var eq = new Equalizer(Rate, Channels);
        eq.Configure(new EqualizerSettings { Enabled = true });

        Assert.Equal(0, GainDb(eq, 1000), 0.01);
    }
}

public class DynamicsTests
{
    [Fact]
    public void NoiseGate_SilencesQuietNoise()
    {
        var gate = new NoiseGate(Rate, Channels);
        gate.Configure(new NoiseGateSettings { Enabled = true, ThresholdDb = -50 });
        double amplitude = Math.Pow(10, -70 / 20.0);

        float[] output = Run(gate, Sine(1000, amplitude, 1));

        Assert.True(Amplitude(output, 1000, 0.5, 0.5) < amplitude * 1e-3);
    }

    [Fact]
    public void NoiseGate_LetsTheVoiceThrough()
    {
        var gate = new NoiseGate(Rate, Channels);
        gate.Configure(new NoiseGateSettings { Enabled = true, ThresholdDb = -50 });

        float[] output = Run(gate, Sine(1000, 0.1, 1));

        Assert.Equal(0, Db(Amplitude(output, 1000, 0.5, 0.5) / 0.1), 0.1);
    }

    [Fact]
    public void Compressor_FollowsTheRatioAboveTheThreshold()
    {
        var compressor = new Compressor(Rate, Channels);
        compressor.Configure(new CompressorSettings { Enabled = true, ThresholdDb = -20, Ratio = 4, MakeupDb = 0 });

        // −6 dBFS constante: 14 dB por encima del umbral → 14/4 = 3,5 dB por encima → −16,5 dBFS.
        float[] output = Run(compressor, Constant(0.5f, 1));

        Assert.Equal(-16.5, Db(output[^1]), 0.05);
    }

    [Fact]
    public void Compressor_BelowTheThreshold_OnlyAppliesMakeup()
    {
        var compressor = new Compressor(Rate, Channels);
        compressor.Configure(new CompressorSettings { Enabled = true, ThresholdDb = -20, Ratio = 4, MakeupDb = 6 });

        float[] output = Run(compressor, Constant(0.01f, 1));

        Assert.Equal(-40 + 6, Db(output[^1]), 0.05);
    }

    [Fact]
    public void Compressor_KneeIsSoft()
    {
        var compressor = new Compressor(Rate, Channels);
        compressor.Configure(new CompressorSettings { ThresholdDb = -20, Ratio = 4 });

        // Justo en el umbral, la rodilla de 6 dB ya reduce (1/4 − 1)·6/8 = −0,5625 dB; 3 dB por debajo, nada.
        Assert.Equal(-0.5625f, compressor.StaticReductionDb(-20), 0.001f);
        Assert.Equal(0f, compressor.StaticReductionDb(-23));
    }

    [Fact]
    public void Limiter_NeverExceedsTheCeiling()
    {
        var limiter = new Limiter(Rate, Channels);
        limiter.Configure(new LimiterSettings { Enabled = true, CeilingDb = -6 });

        float[] output = Run(limiter, Sine(1000, 1.0, 1));

        Assert.True(Peak(output) <= Math.Pow(10, -6 / 20.0) + 1e-4);
    }
}

public class PitchShifterTests
{
    private static float[] Shift(float semitones, double frequency)
    {
        var shifter = new PitchShifter(Rate, Channels);
        shifter.Configure(new PitchShiftSettings { Enabled = true, Semitones = semitones });
        return Run(shifter, Sine(frequency, 0.5, 2));
    }

    /// <summary>
    /// Frecuencia por cruces por cero. No se mide con Goertzel en la frecuencia exacta: este método reparte la energía
    /// en bandas separadas por la velocidad del cruce entre lecturas (25 Hz con una octava), no en un único tono.
    /// </summary>
    private static double ZeroCrossingFrequency(float[] interleaved, double fromSeconds, double seconds)
    {
        int start = (int)(fromSeconds * Rate) + 1;
        int end = start + (int)(seconds * Rate);
        int crossings = 0;
        for (int i = start; i < end; i++)
        {
            if ((interleaved[i * Channels] >= 0) != (interleaved[(i - 1) * Channels] >= 0))
            {
                crossings++;
            }
        }

        return crossings / (2 * seconds);
    }

    [Theory]
    [InlineData(12, 880)]
    [InlineData(-12, 220)]
    [InlineData(7, 659.3)]
    [InlineData(-5, 329.6)]
    public void ShiftsByTheRequestedSemitones(float semitones, double expected)
    {
        float[] output = Shift(semitones, 440);

        Assert.Equal(expected, ZeroCrossingFrequency(output, 0.5, 1), expected * 0.03);
        Assert.True(Amplitude(output, 440, 0.5, 1) < 0.02, "no queda la frecuencia original");
        Assert.True(Peak(output, 0.5, 1) > 0.3, "conserva el nivel");
    }

    [Fact]
    public void ZeroSemitones_IsAnExactPassThrough()
    {
        float[] input = Sine(440, 0.5, 0.5);
        var shifter = new PitchShifter(Rate, Channels);
        shifter.Configure(new PitchShiftSettings { Enabled = true, Semitones = 0 });

        Assert.Equal(input, Run(shifter, input));
    }
}

public class ColorEffectTests
{
    [Fact]
    public void Distortion_AddsHarmonicsWithoutRaisingTheLevel()
    {
        var distortion = new Distortion(Rate, Channels);
        distortion.Configure(new DistortionSettings { Enabled = true, DriveDb = 24, ToneHz = 12000, Mix = 1 });

        float[] output = Run(distortion, Sine(1000, 0.25, 1));

        double fundamental = Amplitude(output, 1000, 0.2, 0.8);
        Assert.True(Amplitude(output, 3000, 0.2, 0.8) / fundamental > Math.Pow(10, -25 / 20.0));
        Assert.Equal(0, Db(fundamental / 0.25), 3.0);
    }

    [Fact]
    public void Distortion_WithoutDrive_StaysClean()
    {
        var distortion = new Distortion(Rate, Channels);
        distortion.Configure(new DistortionSettings { Enabled = true, DriveDb = 0, ToneHz = 12000, Mix = 1 });

        float[] output = Run(distortion, Sine(1000, 0.25, 1));

        Assert.True(Amplitude(output, 3000, 0.2, 0.8) / Amplitude(output, 1000, 0.2, 0.8) < 0.01);
    }

    [Fact]
    public void Distortion_NeverExceedsFullScale()
    {
        var distortion = new Distortion(Rate, Channels);
        distortion.Configure(new DistortionSettings { Enabled = true, DriveDb = 40, ToneHz = 12000, Mix = 1 });

        Assert.True(Peak(Run(distortion, Sine(100, 1.0, 1))) <= 1.0f);
    }

    [Fact]
    public void Robot_MovesTheVoiceToSidebands()
    {
        var modulation = new Modulation(Rate, Channels);
        modulation.Configure(new ModulationSettings { Enabled = true, Mode = ModulationMode.Robot, RobotFrequencyHz = 100, Mix = 1 });

        float[] output = Run(modulation, Sine(1000, 0.5, 1));

        // Seno · seno = dos bandas laterales de la mitad de amplitud (900 y 1100 Hz) y nada en la original.
        Assert.Equal(0.25, Amplitude(output, 900, 0, 1), 0.01);
        Assert.Equal(0.25, Amplitude(output, 1100, 0, 1), 0.01);
        Assert.True(Amplitude(output, 1000, 0, 1) < 0.005);
    }

    [Theory]
    [InlineData(ModulationMode.Chorus)]
    [InlineData(ModulationMode.Flanger)]
    public void ChorusAndFlanger_AreStable(ModulationMode mode)
    {
        var modulation = new Modulation(Rate, Channels);
        modulation.Configure(new ModulationSettings { Enabled = true, Mode = mode, RateHz = 2, Depth = 1, Mix = 0.5f });
        float[] input = Noise(0.5, 3);

        float[] output = Run(modulation, input);

        Assert.True(AllFinite(output));
        Assert.True(Peak(output) < 4);
        Assert.NotEqual(input, output);
    }
}

public class TimeEffectTests
{
    [Fact]
    public void Echo_RepeatsAtItsTimeAndLevel()
    {
        var echo = new Echo(Rate, Channels);
        echo.Configure(new EchoSettings { Enabled = true, DelayMilliseconds = 300, Feedback = 0.5f, Mix = 0.4f });
        echo.Reset();

        // El impulso llega tras 20 ms de silencio: los primeros 10 ms tras activar el eco se graban con rampa.
        const int start = 20 * Rate / 1000;
        float[] output = Run(echo, Concat(Constant(0, 0.02), Impulse(1)));
        int delay = 300 * Rate / 1000;

        Assert.Equal(1f, output[start * Channels]);
        Assert.Equal(0f, Peak(output, (start + 1.0) / Rate, (delay - 2.0) / Rate));
        Assert.Equal(0.4f, output[(start + delay) * Channels], 1e-4f);

        // La segunda repetición pasa por el pasabajos (ganancia 1 en continua): su suma conserva mix · feedback.
        double second = 0;
        for (int i = start + (2 * delay) - 50; i < start + (2 * delay) + 400; i++)
        {
            second += output[i * Channels];
        }

        Assert.Equal(0.2, second, 0.01);
    }

    [Fact]
    public void Echo_MaximumFeedback_DiesAway()
    {
        var echo = new Echo(Rate, Channels);
        echo.Configure(new EchoSettings { Enabled = true, DelayMilliseconds = 100, Feedback = 0.9f, Mix = 1 });

        float[] output = Run(echo, Concat(Noise(0.5, 1), Constant(0, 10)));

        Assert.True(AllFinite(output));
        Assert.True(Peak(output, 10) < 0.1 * Peak(output, 0, 1));
    }

    [Fact]
    public void Reverb_HasATailThatDecays()
    {
        var reverb = new Reverb(Rate, Channels);
        reverb.Configure(new ReverbSettings { Enabled = true, RoomSize = 0.6f, Damping = 0.5f, Mix = 0.5f });

        float[] output = Run(reverb, Impulse(4));

        double early = Energy(output, 0.05, 0.45);
        Assert.True(early > 1e-4, $"cola: {early}");
        Assert.True(Energy(output, 3.0, 0.5) < early * 1e-3);
    }

    [Fact]
    public void Reverb_LargestRoom_IsStable()
    {
        // Al 100 % Freeverb realimenta un 98 %: es casi infinita y suena fuerte (los limitadores de las mezclas recogen
        // el pico), pero tiene que acabar apagándose y no producir valores inválidos.
        var reverb = new Reverb(Rate, Channels);
        reverb.Configure(new ReverbSettings { Enabled = true, RoomSize = 1, Damping = 0, Mix = 1 });

        float[] output = Run(reverb, Concat(Noise(0.5, 1), Constant(0, 8)));

        Assert.True(AllFinite(output));
        Assert.True(Peak(output, 8, 1) < 0.1 * Peak(output, 0, 2));
    }
}

public class VoiceEffectChainTests
{
    private static float[] Run(VoiceEffectChain chain, float[] input, VoiceEffectSettings settings)
    {
        float[] data = (float[])input.Clone();
        const int block = 480 * Channels;
        for (int offset = 0; offset < data.Length; offset += block)
        {
            chain.Process(data.AsSpan(offset, Math.Min(block, data.Length - offset)), settings);
        }

        return data;
    }

    private static VoiceEffectChain NewChain() => new(Rate, Channels, maxBlockFrames: 2048);

    [Fact]
    public void DefaultSettings_AreAnExactPassThrough()
    {
        float[] input = Noise(0.5, 1);

        Assert.Equal(input, Run(NewChain(), input, new VoiceEffectSettings().Normalize()));
    }

    [Fact]
    public void TogglingAnEffect_DoesNotClick()
    {
        // Un cambio brusco a la voz con tono cambiado (retrasada ~20 ms) saltaría hasta casi 1; el cruce lo evita.
        VoiceEffectChain chain = NewChain();
        VoiceEffectSettings off = new VoiceEffectSettings().Normalize();
        VoiceEffectSettings on = (off with { PitchShift = new() { Enabled = true, Semitones = 5 } }).Normalize();
        float[] input = Sine(200, 0.5, 0.5);

        float[] output = Concat(Run(chain, input, off), Run(chain, input, on), Run(chain, input, off));

        Assert.True(MaxStep(output) < 0.06, $"salto máximo: {MaxStep(output)}");
    }

    [Fact]
    public void MasterSwitch_FadesBackToTheDryVoice()
    {
        VoiceEffectChain chain = NewChain();
        VoiceEffectSettings robot = (VoicePresets.All.Single(p => p.Id == "robot").Settings).Normalize();
        float[] input = Sine(200, 0.5, 0.5);

        Run(chain, input, robot);
        float[] output = Run(chain, input, robot with { Enabled = false });

        Assert.True(MaxStep(output) < 0.06, $"salto máximo: {MaxStep(output)}");
        Assert.Equal(input[^200..], output[^200..]);
    }

    [Fact]
    public void Order_ChangesTheResult()
    {
        VoiceEffectSettings boostThenLimit = new VoiceEffectSettings
        {
            Compressor = new() { Enabled = true, Ratio = 1, MakeupDb = 12 },
            Limiter = new() { Enabled = true, CeilingDb = -6 },
        }.Normalize();
        VoiceEffectSettings limitThenBoost = (boostThenLimit with
        {
            Order = [VoiceEffectKind.Limiter, VoiceEffectKind.Compressor],
        }).Normalize();
        float[] input = Constant(0.5f, 0.5);

        Assert.True(Peak(Run(NewChain(), input, boostThenLimit), 0.1) <= 0.502f);
        Assert.True(Peak(Run(NewChain(), input, limitThenBoost), 0.1) > 1.5f);
    }

    [Fact]
    public void EveryPreset_RunsCleanly()
    {
        foreach (VoicePreset preset in VoicePresets.All)
        {
            float[] output = Run(NewChain(), Noise(0.3, 1, seed: 7), preset.Settings.Normalize());

            Assert.True(AllFinite(output), preset.Id);
            Assert.True(Peak(output) < 2, preset.Id);
        }
    }
}

public class VoiceEffectSettingsTests
{
    [Fact]
    public void Normalize_FixesOrderAndRanges()
    {
        VoiceEffectSettings broken = new VoiceEffectSettings
        {
            Order = [VoiceEffectKind.Echo, VoiceEffectKind.Echo, VoiceEffectKind.Reverb, (VoiceEffectKind)99],
            NoiseGate = new() { ThresholdDb = -200 },
            Compressor = new() { Ratio = 0, AttackMilliseconds = float.NaN },
            Modulation = new() { Mode = (ModulationMode)99 },
            Echo = null!,
        }.Normalize();

        Assert.Equal(VoiceEffectSettings.DefaultOrder.Count, broken.Order.Count);
        Assert.Equal([VoiceEffectKind.Echo, VoiceEffectKind.Reverb, VoiceEffectKind.NoiseGate], broken.Order.Take(3));
        Assert.Equal(-80, broken.NoiseGate.ThresholdDb);
        Assert.Equal(1, broken.Compressor.Ratio);
        Assert.Equal(CompressorSettings.AttackRange.Min, broken.Compressor.AttackMilliseconds);
        Assert.Equal(ModulationMode.Robot, broken.Modulation.Mode);
        Assert.Equal(new EchoSettings(), broken.Echo);
    }

    [Fact]
    public void Presets_AreWithinRange()
    {
        foreach (VoicePreset preset in VoicePresets.All)
        {
            VoiceEffectSettings normalized = preset.Settings.Normalize();
            Assert.Equal(preset.Settings with { Order = normalized.Order }, normalized);
        }
    }
}
