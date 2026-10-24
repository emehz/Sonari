using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sonari.Audio.Core.Effects;

namespace Sonari.Presentation.ViewModels;

/// <summary>Un deslizador de un efecto. Avisa al efecto de cada cambio.</summary>
public sealed partial class EffectParameterViewModel : ObservableObject
{
    private readonly Func<double, string> _format;
    private readonly Action _changed;
    private bool _silent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private double _value;

    [ObservableProperty]
    private bool _isVisible = true;

    public EffectParameterViewModel(string key, string label, ParameterRange range, double step,
        Func<double, string> format, Action changed)
    {
        Key = key;
        Label = label;
        Minimum = range.Min;
        Maximum = range.Max;
        Step = step;
        _format = format;
        _changed = changed;
    }

    public string Key { get; }

    public string Label { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Step { get; }

    public string ValueText => _format(Value);

    /// <summary>Cambia el valor sin avisar (al cargar una configuración).</summary>
    public void Load(double value)
    {
        _silent = true;
        try
        {
            Value = value;
        }
        finally
        {
            _silent = false;
        }
    }

    partial void OnValueChanged(double value)
    {
        if (!_silent)
        {
            _changed();
        }
    }
}

public sealed record ModulationModeOption(ModulationMode Mode, string Name)
{
    public override string ToString() => Name;
}

/// <summary>La tarjeta de un efecto: interruptor, parámetros y posición en la cadena.</summary>
public sealed partial class EffectViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _loading;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private ModulationModeOption? _selectedMode;

    [ObservableProperty]
    private bool _canMoveUp;

    [ObservableProperty]
    private bool _canMoveDown;

    public EffectViewModel(VoiceEffectKind kind, string title, string description, Action changed)
    {
        Kind = kind;
        Title = title;
        Description = description;
        _changed = changed;
    }

    public VoiceEffectKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    public List<EffectParameterViewModel> Parameters { get; } = [];

    /// <summary>Solo para la modulación.</summary>
    public IReadOnlyList<ModulationModeOption>? Modes { get; init; }

    public bool HasModes => Modes is not null;

    public EffectParameterViewModel Parameter(string key) => Parameters.First(p => p.Key == key);

    public float this[string key] => (float)Parameter(key).Value;

    public EffectParameterViewModel Add(string key, string label, ParameterRange range, double step, Func<double, string> format)
    {
        var parameter = new EffectParameterViewModel(key, label, range, step, format, OnParameterChanged);
        Parameters.Add(parameter);
        return parameter;
    }

    /// <summary>Carga valores sin que cada uno dispare un cambio en el motor.</summary>
    public void Load(bool enabled, IEnumerable<(string Key, double Value)> values, ModulationMode? mode = null)
    {
        _loading = true;
        try
        {
            IsEnabled = enabled;
            foreach (var (key, value) in values)
            {
                Parameter(key).Load(value);
            }

            if (mode is not null && Modes is not null)
            {
                SelectedMode = Modes.First(m => m.Mode == mode);
            }

            UpdateVisibility();
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnIsEnabledChanged(bool value) => Notify();

    partial void OnSelectedModeChanged(ModulationModeOption? value)
    {
        UpdateVisibility();
        Notify();
    }

    private void OnParameterChanged() => Notify();

    private void Notify()
    {
        if (!_loading)
        {
            _changed();
        }
    }

    /// <summary>La modulación muestra solo los parámetros del modo elegido.</summary>
    private void UpdateVisibility()
    {
        if (Kind != VoiceEffectKind.Modulation || SelectedMode is null)
        {
            return;
        }

        bool robot = SelectedMode.Mode == ModulationMode.Robot;
        Parameter("robot").IsVisible = robot;
        Parameter("rate").IsVisible = !robot;
        Parameter("depth").IsVisible = !robot;
    }

    internal static string Db(double value) => $"{value.ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture)} dB";

    internal static string Hz(double value) => value >= 1000
        ? $"{(value / 1000).ToString("0.0", CultureInfo.CurrentCulture)} kHz"
        : $"{value.ToString(value < 10 ? "0.00" : "0", CultureInfo.CurrentCulture)} Hz";

    internal static string Ms(double value) => $"{value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} ms";

    internal static string Percent(double value) => $"{Math.Round(value * 100).ToString(CultureInfo.CurrentCulture)} %";
}
