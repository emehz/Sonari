using OndaMix.Audio.Core.Formats;

namespace OndaMix.Presentation.ViewModels;

/// <summary>
/// Comportamiento visual de un medidor: sube al instante, cae a velocidad constante y mantiene una marca del pico
/// durante un momento. Así un golpe corto se ve aunque dure menos que un fotograma.
/// </summary>
public sealed class MeterBallistics
{
    private const double FallDbPerSecond = 24;
    private static readonly TimeSpan PeakHold = TimeSpan.FromSeconds(1.5);

    private DateTime _peakSetAt;

    public double LevelDb { get; private set; } = Decibels.Floor;

    public double PeakDb { get; private set; } = Decibels.Floor;

    public void Update(float linearPeak, TimeSpan elapsed, DateTime now)
    {
        double db = Decibels.FromLinear(linearPeak);
        double fallen = LevelDb - (FallDbPerSecond * elapsed.TotalSeconds);
        LevelDb = Math.Max(db, Math.Max(fallen, Decibels.Floor));

        if (db >= PeakDb || now - _peakSetAt > PeakHold)
        {
            PeakDb = db;
            _peakSetAt = now;
        }
    }

    public void Reset()
    {
        LevelDb = Decibels.Floor;
        PeakDb = Decibels.Floor;
    }
}
