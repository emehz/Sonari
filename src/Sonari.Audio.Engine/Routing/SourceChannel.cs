using Sonari.Audio.Core.Engine;

namespace Sonari.Audio.Engine.Routing;

/// <summary>
/// Una fuente tal y como la ve el resto de la aplicación: identidad, controles y estado. Vive mientras el usuario no la
/// quite; el <see cref="MixerChannel"/> (buffers y medidores) solo existe mientras el motor está en marcha.
/// </summary>
internal sealed class SourceChannel : ISourceChannel
{
    private readonly Action _onChanged;
    private readonly object _statusLock = new();
    private SourceStatus _status = SourceStatus.Stopped;
    private string _statusMessage = "Motor detenido";
    private int? _processId;
    private ChannelStrip? _mixer;

    public SourceChannel(SourceKind kind, string displayName, ApplicationTarget? target, Action onChanged)
    {
        Kind = kind;
        DisplayName = displayName;
        Target = target;
        _onChanged = onChanged;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public SourceKind Kind { get; }

    public string DisplayName { get; }

    public ApplicationTarget? Target { get; }

    public string? ExecutablePath => Target?.ExecutablePath;

    public ChannelControl Control { get; } = new();

    public SourceStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public string StatusMessage
    {
        get
        {
            lock (_statusLock)
            {
                return _statusMessage;
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_statusLock)
            {
                return _processId;
            }
        }
    }

    public ChannelStrip? Mixer
    {
        get => Volatile.Read(ref _mixer);
        set => Volatile.Write(ref _mixer, value);
    }

    public float Balance
    {
        get => Control.Balance;
        set => Control.Balance = value;
    }

    public float GainDb
    {
        get => Control.GainDb;
        set => Control.GainDb = value;
    }

    public bool IsMuted
    {
        get => Control.IsMuted;
        set => Control.IsMuted = value;
    }

    public bool SendToMonitor
    {
        get => Control.SendToMonitor;
        set => Control.SendToMonitor = value;
    }

    public ChannelMetrics ReadMetrics() => Mixer?.ReadMetrics() ?? default;

    public void SetStatus(SourceStatus status, string message, int? processId = null)
    {
        lock (_statusLock)
        {
            if (_status == status && _statusMessage == message && _processId == processId)
            {
                return;
            }

            _status = status;
            _statusMessage = message;
            _processId = processId;
        }

        _onChanged();
    }

    public override string ToString() => $"{Kind} «{DisplayName}»";
}
