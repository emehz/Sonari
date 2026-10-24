using Sonari.Audio.Core.Engine;
using Sonari.Audio.Core.Formats;

namespace Sonari.Audio.Engine.Routing;

/// <summary>Valores de un canal escritos por la UI y leídos por el hilo de audio una vez por bloque.</summary>
internal sealed class ChannelControl : IChannelControl
{
    public const float MinGainDb = -60f;
    public const float MaxGainDb = 24f;

    private float _gainDb;
    private float _balance;
    private int _muted;
    private int _sendToMonitor;
    private int _sendToVirtual = 1;

    public float Balance
    {
        get => Volatile.Read(ref _balance);
        set => Volatile.Write(ref _balance, float.IsNaN(value) ? 0f : Math.Clamp(value, -1f, 1f));
    }

    /// <summary>
    /// Ganancias lineales izquierda/derecha: fader por balance. Ley de balance (no de panorama): en el centro ambos lados
    /// quedan a 1, y al girar solo se atenúa el lado contrario.
    /// </summary>
    public (float Left, float Right) StereoGains
    {
        get
        {
            float fader = FaderLinear;
            float balance = Balance;
            return (fader * (balance > 0 ? 1 - balance : 1), fader * (balance < 0 ? 1 + balance : 1));
        }
    }

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

    /// <summary>Por defecto activado: enviar al micrófono virtual es para lo que se añaden las fuentes.</summary>
    public bool SendToVirtual
    {
        get => Volatile.Read(ref _sendToVirtual) != 0;
        set => Volatile.Write(ref _sendToVirtual, value ? 1 : 0);
    }

    public bool SendsTo(BusKind bus) => bus == BusKind.Monitor ? SendToMonitor : SendToVirtual;

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
