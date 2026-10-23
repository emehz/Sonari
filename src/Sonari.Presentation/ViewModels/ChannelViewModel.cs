using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;
using Sonari.Presentation.Interop;

namespace Sonari.Presentation.ViewModels;

/// <summary>Tira de canal del mezclador para una fuente del motor. Los controles escriben directamente en el motor.</summary>
public sealed partial class ChannelViewModel : ObservableObject
{
    private readonly ISourceChannel _source;
    private readonly MeterBallistics _meter = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    private double _gainDb;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _sendToMonitor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BalanceText))]
    private double _balance;

    [ObservableProperty]
    private SourceStatus _status;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _levelDb = Decibels.Floor;

    [ObservableProperty]
    private double _peakDb = Decibels.Floor;

    [ObservableProperty]
    private string _diagnostics = string.Empty;

    public ChannelViewModel(ISourceChannel source)
    {
        _source = source;
        _gainDb = source.GainDb;
        _isMuted = source.IsMuted;
        _sendToMonitor = source.SendToMonitor;
        _balance = source.Balance;
        Icon = ShellIcons.ForExecutable(source.ExecutablePath);
        RefreshStatus();
    }

    public ISourceChannel Source => _source;

    public Guid Id => _source.Id;

    public string DisplayName => _source.DisplayName;

    public SourceKind Kind => _source.Kind;

    public bool IsMicrophone => Kind == SourceKind.Microphone;

    /// <summary>El micrófono y el canal de sonidos son fijos; las aplicaciones y el sistema se pueden quitar.</summary>
    public bool CanRemove => Kind is SourceKind.Application or SourceKind.SystemAudio;

    public ImageSource? Icon { get; }

    public bool HasIcon => Icon is not null;

    /// <summary>Icono de Segoe para cuando no hay icono del ejecutable.</summary>
    public string Glyph => Kind switch
    {
        SourceKind.Microphone => "\uE720",
        SourceKind.SystemAudio => "\uE7F5",
        SourceKind.SoundPlayer => "\uE8D6",
        _ => "\uE71D",
    };

    public string MonitorHint => Kind switch
    {
        SourceKind.Microphone => "Usa auriculares: con altavoces el micrófono capta su propia salida y se acopla.",
        SourceKind.SoundPlayer => "Activado: oyes los sonidos que lanzas desde el panel.",
        _ => "Ya oyes esta fuente directamente; enviarla al monitoreo la duplicaría con retardo.",
    };

    public string BalanceText => Math.Abs(Balance) < 0.01
        ? "Centro"
        : Balance < 0 ? $"I {Math.Round(-Balance * 100)}" : $"D {Math.Round(Balance * 100)}";

    public string GainText => GainDb <= -60 ? "−∞ dB" : $"{GainDb:+0.0;−0.0;0.0} dB";

    public void RefreshStatus()
    {
        Status = _source.Status;
        StatusMessage = _source.StatusMessage;
    }

    public void UpdateMeters(TimeSpan elapsed, DateTime now)
    {
        ChannelMetrics metrics = _source.ReadMetrics();
        _meter.Update(metrics.Input.Max, elapsed, now);
        LevelDb = _meter.LevelDb;
        PeakDb = _meter.PeakDb;

        string buffer = metrics.BufferMilliseconds > 0 ? $"{metrics.BufferMilliseconds:0.0} ms" : "—";
        if (Kind == SourceKind.SoundPlayer)
        {
            // Generado en el propio motor: no tiene buffer de captura ni deriva.
            Diagnostics = string.Empty;
            return;
        }

        string text = $"buffer {buffer} · deriva {metrics.DriftPpm:+0;-0;0} ppm · vaciados {metrics.Underruns}";
        if (!IsMicrophone)
        {
            text += $" · pausas {metrics.IdleGaps}";
        }

        if (metrics.OverrunSamples > 0)
        {
            text += $" · perdidas {metrics.OverrunSamples}";
        }

        Diagnostics = text;
    }

    public void ResetMeters()
    {
        _meter.Reset();
        LevelDb = _meter.LevelDb;
        PeakDb = _meter.PeakDb;
        Diagnostics = string.Empty;
    }

    partial void OnGainDbChanged(double value) => _source.GainDb = (float)value;

    partial void OnIsMutedChanged(bool value) => _source.IsMuted = value;

    partial void OnSendToMonitorChanged(bool value) => _source.SendToMonitor = value;

    partial void OnBalanceChanged(double value) => _source.Balance = (float)value;
}
