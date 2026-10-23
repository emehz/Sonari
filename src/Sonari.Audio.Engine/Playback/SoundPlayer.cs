using System.Collections.Concurrent;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;

namespace Sonari.Audio.Engine.Playback;

/// <summary>
/// Reproductor de sonidos que genera audio dentro del hilo de render del bus.
/// <list type="bullet">
/// <item>Las órdenes llegan por una cola sin bloqueos y se aplican al principio de cada bloque.</item>
/// <item>Hay <see cref="VoiceCount"/> voces preasignadas: reproducir no asigna memoria en el hilo de audio. Si se llenan,
/// se reutiliza la voz más antigua.</item>
/// <item>Empezar, parar, pausar y cambiar el volumen usan rampas de unos milisegundos, así que nunca hay chasquidos.</item>
/// <item>El estado de cada voz se publica con un contador de versión (seqlock): la UI lo lee sin bloquear al audio.</item>
/// </list>
/// </summary>
public sealed class SoundPlayer : ISoundPlayer
{
    public const int VoiceCount = 32;

    private const double StopFadeMilliseconds = 10;
    private const double EmergencyFadeMilliseconds = 5;
    private const int MaxCommandsPerBlock = 256;

    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly Voice[] _voices = new Voice[VoiceCount];
    private AudioFormat _format = AudioFormat.Default;
    private long _startCounter;
    private int _activeVoices;
    private long _rejectedClips;
    private long _stolenVoices;

    public SoundPlayer()
    {
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new Voice();
        }
    }

    public int MaxVoices => VoiceCount;

    public int ActiveVoices => Volatile.Read(ref _activeVoices);

    /// <summary>Clips ignorados por venir en otro formato (hay que decodificarlos de nuevo).</summary>
    public long RejectedClips => Interlocked.Read(ref _rejectedClips);

    /// <summary>Veces que se reutilizó la voz más antigua porque no quedaban libres.</summary>
    public long StolenVoices => Interlocked.Read(ref _stolenVoices);

    /// <summary>Prepara el reproductor para un arranque del motor. Solo con el render parado; descarta todo lo que sonaba.</summary>
    public void Configure(AudioFormat format)
    {
        _format = format;
        _commands.Clear();
        foreach (Voice voice in _voices)
        {
            voice.Free();
            Publish(voice);
        }

        Volatile.Write(ref _activeVoices, 0);
    }

    public void Play(Guid soundId, SoundClip clip, float gainDb, bool loop)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _commands.Enqueue(new Command(CommandKind.Play, soundId, clip, Decibels.ToLinear(gainDb), loop));
    }

    public void Stop(Guid soundId) => _commands.Enqueue(new Command(CommandKind.Stop, soundId));

    public void Pause(Guid soundId) => _commands.Enqueue(new Command(CommandKind.Pause, soundId));

    public void Resume(Guid soundId) => _commands.Enqueue(new Command(CommandKind.Resume, soundId));

    public void SetGain(Guid soundId, float gainDb) =>
        _commands.Enqueue(new Command(CommandKind.SetGain, soundId, Value: Decibels.ToLinear(gainDb)));

    public void SetLoop(Guid soundId, bool loop) => _commands.Enqueue(new Command(CommandKind.SetLoop, soundId, Flag: loop));

    public void StopAll()
    {
        // Las órdenes pendientes (p. ej. un «reproducir» de hace un instante) se descartan: la parada manda.
        _commands.Clear();
        _commands.Enqueue(new Command(CommandKind.StopAll, Guid.Empty));
    }

    public SoundPlaybackState GetState(Guid soundId)
    {
        SoundPlaybackState best = SoundPlaybackState.Stopped;
        foreach (Voice voice in _voices)
        {
            if (!voice.TryReadPublished(out PublishedVoice published) || published.SoundId != soundId)
            {
                continue;
            }

            if (published.Status == SoundPlaybackStatus.Playing ||
                (published.Status == SoundPlaybackStatus.Paused && best.Status == SoundPlaybackStatus.Stopped))
            {
                best = new SoundPlaybackState(
                    published.Status,
                    TimeSpan.FromSeconds((double)published.Position / published.SampleRate),
                    TimeSpan.FromSeconds((double)published.Frames / published.SampleRate));
            }
        }

        return best;
    }

    /// <summary>Hilo de render: aplica las órdenes pendientes y mezcla todas las voces en <paramref name="output"/>.</summary>
    public void Render(Span<float> output)
    {
        output.Clear();
        ApplyCommands();

        int channels = _format.Channels;
        int frames = output.Length / channels;
        int active = 0;

        foreach (Voice voice in _voices)
        {
            if (voice.State == VoiceState.Free)
            {
                continue;
            }

            if (voice.State == VoiceState.Playing)
            {
                MixVoice(voice, output, frames, channels);
            }

            if (voice.State != VoiceState.Free)
            {
                active++;
            }

            Publish(voice);
        }

        Volatile.Write(ref _activeVoices, active);
    }

    // Sin lambdas: una clausura asignaría memoria en el hilo de audio por cada orden.
    private void ApplyCommands()
    {
        for (int i = 0; i < MaxCommandsPerBlock && _commands.TryDequeue(out Command command); i++)
        {
            if (command.Kind == CommandKind.Play)
            {
                StartVoice(command);
                continue;
            }

            if (command.Kind == CommandKind.StopAll)
            {
                foreach (Voice voice in _voices)
                {
                    if (voice.State == VoiceState.Paused)
                    {
                        voice.Free();
                    }
                    else if (voice.State == VoiceState.Playing)
                    {
                        voice.BeginFadeOut(FadeFrames(EmergencyFadeMilliseconds), pauseAfter: false);
                    }
                }

                continue;
            }

            foreach (Voice voice in _voices)
            {
                if (voice.State == VoiceState.Free || voice.SoundId != command.SoundId || voice.IsStopping)
                {
                    continue;
                }

                switch (command.Kind)
                {
                    case CommandKind.Stop:
                        voice.BeginFadeOut(FadeFrames(StopFadeMilliseconds), pauseAfter: false);
                        break;
                    case CommandKind.Pause when voice.State == VoiceState.Playing:
                        voice.BeginFadeOut(FadeFrames(StopFadeMilliseconds), pauseAfter: true);
                        break;
                    case CommandKind.Resume:
                        voice.Resume();
                        break;
                    case CommandKind.SetGain:
                        voice.TargetGain = command.Value;
                        break;
                    case CommandKind.SetLoop:
                        voice.Loop = command.Flag;
                        break;
                }
            }
        }
    }

    private void StartVoice(Command command)
    {
        SoundClip clip = command.Clip!;
        if (clip.Format != _format)
        {
            Interlocked.Increment(ref _rejectedClips);
            return;
        }

        // Reproducir un sonido que ya suena lo reinicia: la reproducción anterior se funde en vez de cortarse.
        foreach (Voice voice in _voices)
        {
            if (voice.State != VoiceState.Free && voice.SoundId == command.SoundId && !voice.IsStopping)
            {
                voice.BeginFadeOut(FadeFrames(StopFadeMilliseconds), pauseAfter: false);
            }
        }

        Voice? target = null;
        Voice? oldest = null;
        foreach (Voice voice in _voices)
        {
            if (voice.State == VoiceState.Free)
            {
                target = voice;
                break;
            }

            if (oldest is null || voice.StartOrder < oldest.StartOrder)
            {
                oldest = voice;
            }
        }

        if (target is null)
        {
            target = oldest!;
            Interlocked.Increment(ref _stolenVoices);
        }

        target.Start(command.SoundId, clip, command.Value, command.Flag, ++_startCounter);
    }

    private int FadeFrames(double milliseconds) => Math.Max(1, (int)(_format.SampleRate * milliseconds / 1000));

    private static void MixVoice(Voice voice, Span<float> output, int frames, int channels)
    {
        float[] samples = voice.Clip!.Samples;
        int clipFrames = voice.Clip.Frames;
        float startGain = voice.Gain;
        float gainStep = (voice.TargetGain - startGain) / frames;

        for (int frame = 0; frame < frames; frame++)
        {
            if (voice.Position >= clipFrames)
            {
                if (voice.Loop && clipFrames > 0)
                {
                    voice.Position = 0;
                }
                else
                {
                    voice.Free();
                    return;
                }
            }

            float gain = startGain + (gainStep * (frame + 1));
            if (voice.IsStopping)
            {
                // (restante − 1) / total: termina exactamente en 0 y, sumado a la rampa de entrada de un reinicio, da
                // un fundido cruzado sin exceso de nivel.
                gain *= (float)(voice.FadeRemaining - 1) / voice.FadeTotal;
                if (--voice.FadeRemaining <= 0)
                {
                    voice.FinishFadeOut();
                    return;
                }
            }

            int source = (int)voice.Position * channels;
            int destination = frame * channels;
            for (int c = 0; c < channels; c++)
            {
                output[destination + c] += samples[source + c] * gain;
            }

            voice.Position++;
        }

        voice.Gain = voice.TargetGain;
    }

    private static void Publish(Voice voice)
    {
        Interlocked.Increment(ref voice.Version);
        voice.PublishedSoundId = voice.SoundId;
        voice.PublishedStatus = voice.State switch
        {
            VoiceState.Playing when !voice.IsStopping => SoundPlaybackStatus.Playing,
            VoiceState.Paused => SoundPlaybackStatus.Paused,
            _ => SoundPlaybackStatus.Stopped,
        };
        voice.PublishedPosition = voice.Position;
        voice.PublishedFrames = voice.Clip?.Frames ?? 0;
        voice.PublishedSampleRate = voice.Clip?.Format.SampleRate ?? 1;
        Interlocked.Increment(ref voice.Version);
    }

    private enum CommandKind
    {
        Play,
        Stop,
        Pause,
        Resume,
        SetGain,
        SetLoop,
        StopAll,
    }

    private readonly record struct Command(
        CommandKind Kind,
        Guid SoundId,
        SoundClip? Clip = null,
        float Value = 0,
        bool Flag = false);

    private readonly record struct PublishedVoice(Guid SoundId, SoundPlaybackStatus Status, long Position, long Frames, int SampleRate);

    private enum VoiceState
    {
        Free,
        Playing,
        Paused,
    }

    private sealed class Voice
    {
        // Estado del hilo de audio.
        public VoiceState State;
        public Guid SoundId;
        public SoundClip? Clip;
        public long Position;
        public float Gain;
        public float TargetGain;
        public bool Loop;
        public int FadeRemaining;
        public int FadeTotal;
        public bool PauseAfterFade;
        public long StartOrder;

        // Estado publicado para la UI, protegido por Version (par = estable, impar = escribiéndose).
        public int Version;
        public Guid PublishedSoundId;
        public SoundPlaybackStatus PublishedStatus;
        public long PublishedPosition;
        public long PublishedFrames;
        public int PublishedSampleRate = 1;

        public bool IsStopping => FadeRemaining > 0;

        public void Start(Guid soundId, SoundClip clip, float gain, bool loop, long order)
        {
            SoundId = soundId;
            Clip = clip;
            Position = 0;
            Gain = 0f;            // la rampa del primer bloque hace de fundido de entrada
            TargetGain = gain;
            Loop = loop;
            FadeRemaining = 0;
            PauseAfterFade = false;
            StartOrder = order;
            State = VoiceState.Playing;
        }

        public void BeginFadeOut(int frames, bool pauseAfter)
        {
            if (State == VoiceState.Paused && !pauseAfter)
            {
                Free();
                return;
            }

            FadeTotal = frames;
            FadeRemaining = frames;
            PauseAfterFade = pauseAfter;
        }

        public void FinishFadeOut()
        {
            FadeRemaining = 0;
            if (PauseAfterFade)
            {
                PauseAfterFade = false;
                State = VoiceState.Paused;
            }
            else
            {
                Free();
            }
        }

        public void Resume()
        {
            if (State == VoiceState.Paused)
            {
                Gain = 0f;
                State = VoiceState.Playing;
            }
        }

        public void Free()
        {
            State = VoiceState.Free;
            Clip = null;
            SoundId = Guid.Empty;
            FadeRemaining = 0;
            PauseAfterFade = false;
        }

        public bool TryReadPublished(out PublishedVoice published)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int before = Volatile.Read(ref Version);
                if ((before & 1) != 0)
                {
                    continue;
                }

                published = new PublishedVoice(
                    PublishedSoundId, PublishedStatus, PublishedPosition, PublishedFrames, PublishedSampleRate);
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref Version) == before)
                {
                    return true;
                }
            }

            published = default;
            return false;
        }
    }
}
