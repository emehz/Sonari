using Sonari.Audio.Core.Engine;
using Sonari.Audio.Engine.Dsp;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Procesado común a todos los canales en el hilo de render: obtener la entrada, aplicar fader y balance, medir y
/// enviar al bus. Las subclases solo deciden de dónde sale la entrada.
/// </summary>
internal abstract class ChannelStrip(ChannelControl control, int channels)
{
    private readonly StereoGain _faderAndBalance = new();
    private readonly SmoothedGain _monitorSend = new(0f);

    protected ChannelControl Control { get; } = control;

    protected int Channels { get; } = channels;

    public PeakMeter InputMeter { get; } = new();

    public PeakMeter PostFaderMeter { get; } = new();

    /// <summary>Hilo de render. <paramref name="scratch"/> debe tener al menos el tamaño de <paramref name="mix"/>.</summary>
    public void MixInto(Span<float> mix, Span<float> scratch)
    {
        Span<float> buffer = scratch[..mix.Length];

        // Se lee siempre, aunque el envío esté a cero: las fuentes capturadas deben vaciar su buffer y compensar deriva.
        ReadInput(buffer);

        (float left, float right) = Control.StereoGains;
        _faderAndBalance.Apply(buffer, Channels, left, right);
        PostFaderMeter.Process(buffer, Channels);

        _monitorSend.Target = Control.SendToMonitor ? 1f : 0f;
        if (_monitorSend.Current == 0f && _monitorSend.Target == 0f)
        {
            return;
        }

        _monitorSend.Apply(buffer, Channels);
        for (int i = 0; i < mix.Length; i++)
        {
            mix[i] += buffer[i];
        }
    }

    /// <summary>Solo con la salida parada.</summary>
    public virtual void ResetForRestart()
    {
        (float left, float right) = Control.StereoGains;
        _faderAndBalance.Snap(left, right);
        _monitorSend.Target = Control.SendToMonitor ? 1f : 0f;
        _monitorSend.Snap();
    }

    public abstract ChannelMetrics ReadMetrics();

    /// <summary>Hilo de render: rellena <paramref name="buffer"/> entero con la entrada de este canal.</summary>
    protected abstract void ReadInput(Span<float> buffer);
}
