using System.Windows;
using System.Windows.Media;

namespace Sonari.Presentation.Controls;

/// <summary>
/// Medidor horizontal en dBFS dibujado con <see cref="OnRender"/>: barato aunque se actualice 30 veces por segundo
/// en varios canales. Verde hasta −18 dB, amarillo hasta −6 dB y rojo por encima.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public const double MinDb = -60;
    public const double MaxDb = 0;

    public static readonly DependencyProperty LevelDbProperty = DependencyProperty.Register(
        nameof(LevelDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(MinDb, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakDbProperty = DependencyProperty.Register(
        nameof(PeakDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(MinDb, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1B)));
    private static readonly Brush PeakBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE7, 0xE9, 0xEE)));
    private static readonly Brush FillBrush = Freeze(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(Color.FromRgb(0x34, 0xD3, 0x99), 0.0),
            new(Color.FromRgb(0x34, 0xD3, 0x99), Position(-18)),
            new(Color.FromRgb(0xFB, 0xBF, 0x24), Position(-12)),
            new(Color.FromRgb(0xFB, 0xBF, 0x24), Position(-6)),
            new(Color.FromRgb(0xF8, 0x71, 0x71), Position(-3)),
            new(Color.FromRgb(0xF8, 0x71, 0x71), 1.0),
        },
        new Point(0, 0),
        new Point(1, 0)));

    public double LevelDb
    {
        get => (double)GetValue(LevelDbProperty);
        set => SetValue(LevelDbProperty, value);
    }

    public double PeakDb
    {
        get => (double)GetValue(PeakDbProperty);
        set => SetValue(PeakDbProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, 8);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        var track = new Rect(0, 0, width, height);
        dc.DrawRoundedRectangle(TrackBrush, null, track, 2, 2);

        double fill = Position(LevelDb) * width;
        if (fill > 0.5)
        {
            // El degradado se calcula sobre el ancho total, así que el color depende del nivel y no del tamaño de la barra.
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, fill, height), 2, 2));
            dc.DrawRectangle(FillBrush, null, track);
            dc.Pop();
        }

        double peak = Position(PeakDb) * width;
        if (peak > 1)
        {
            dc.DrawRectangle(PeakBrush, null, new Rect(Math.Min(peak, width - 2), 0, 2, height));
        }
    }

    private static double Position(double db) => Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0, 1);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
