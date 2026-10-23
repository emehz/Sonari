using OndaMix.Audio.Core.Engine;
using OndaMix.Audio.Core.Formats;

namespace OndaMix.Audio.Engine.Routing;

/// <summary>Valores de un canal escritos por la UI y leídos por el hilo de audio una vez por bloque.</summary>
internal sealed class ChannelControl : IChannelControl
{
    public const float MinGainDb = -60f;
    public const float MaxGainDb = 24f;

    private float _gainDb;
    private int _muted;
    private int _sendToMonitor;

    public float GainDb
    {
        get => Volatile.Read(ref _gainDb);
        set => Volatile.Write(ref _gainDb, Math.Clamp(value, MinGainDb, MaxGainDb));
    }

    public bool IsMuted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    public bool SendToMonitor
    {
        get => Volatile.Read(ref _sendToMonitor) != 0;
        set => Volatile.Write(ref _sendToMonitor, value ? 1 : 0);
    }

    /// <summary>Ganancia lineal del fader, con el silencio ya aplicado. El mínimo del fader equivale a silencio total.</summary>
    public float FaderLinear => IsMuted || GainDb <= MinGainDb ? 0f : Decibels.ToLinear(GainDb);
}

internal sealed class BusControl : IBusControl
{
    private float _gainDb;
    private int _muted;

    public float GainDb
    {
        get => Volatile.Read(ref _gainDb);
        set => Volatile.Write(ref _gainDb, Math.Clamp(value, ChannelControl.MinGainDb, ChannelControl.MaxGainDb));
    }

    public bool IsMuted
    {
        get => Volatile.Read(ref _muted) != 0;
        set => Volatile.Write(ref _muted, value ? 1 : 0);
    }

    public float Linear => IsMuted || GainDb <= ChannelControl.MinGainDb ? 0f : Decibels.ToLinear(GainDb);
}
