using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;
using Sonari.Audio.Engine.Playback;

namespace Sonari.UnitTests.Engine;

public class SoundPlayerTests
{
    private const int Block = 480;
    private static readonly AudioFormat Format = AudioFormat.Default;

    private static SoundClip Constant(float value, int frames)
    {
        var samples = new float[frames * 2];
        Array.Fill(samples, value);
        return new SoundClip(samples, Format, "prueba");
    }

    private static SoundPlayer CreatePlayer()
    {
        var player = new SoundPlayer();
        player.Configure(Format);
        return player;
    }

    private static float[] Render(SoundPlayer player, int blocks = 1)
    {
        var output = new float[Block * 2 * blocks];
        for (int b = 0; b < blocks; b++)
        {
            player.Render(output.AsSpan(b * Block * 2, Block * 2));
        }

        return output;
    }

    [Fact]
    public void Play_FadesInOverTheFirstBlockAndThenPlaysAtFullGain()
    {
        SoundPlayer player = CreatePlayer();
        var id = Guid.NewGuid();
        player.Play(id, Constant(0.5f, 48_000), gainDb: 0, loop: false);

        float[] first = Render(player);
        float[] second = Render(player);

        Assert.True(first[0] < 0.01f);
        Assert.Equal(0.5f, first[^1], 1e-5f);
        Assert.All(second, s => Assert.Equal(0.5f, s, 1e-5f));
        Assert.Equal(SoundPlaybackStatus.Playing, player.GetState(id).Status);
    }

    [Fact]
    public void NonLoopingSound_EndsAndReportsStopped()
    {
        SoundPlayer player = CreatePlayer();
        var id = Guid.NewGuid();
        player.Play(id, Constant(0.5f, 1000), 0, loop: false);

        float[] output = Render(player, blocks: 4);

        Assert.Equal(0f, output[(1000 * 2) + 10]);
        Assert.Equal(SoundPlaybackStatus.Stopped, player.GetState(id).Status);
        Assert.Equal(0, player.ActiveVoices);
    }

    [Fact]
    public void LoopingSound_WrapsAround()
    {
        SoundPlayer player = CreatePlayer();
        var id = Guid.NewGuid();
        player.Play(id, Constant(0.5f, 300), 0, loop: true);

        float[] output = Render(player, blocks: 5);

        Assert.All(output.AsSpan(Block * 2).ToArray(), s => Assert.Equal(0.5f, s, 1e-5f));
        Assert.Equal(SoundPlaybackStatus.Playing, player.GetState(id).Status);
    }

    [Fact]
    public void StopAll_SilencesEverythingWithinTheEmergencyFade()
    {
        SoundPlayer player = CreatePlayer();
        for (int i = 0; i < 5; i++)
        {
            player.Play(Guid.NewGuid(), Constant(0.1f, 48_000), 0, loop: true);
        }

        Render(player, blocks: 2);
        player.StopAll();
        float[] output = Render(player, blocks: 2);

        // Fundido de 5 ms = 240 frames: a partir de ahí, silencio absoluto.
        Assert.All(output.AsSpan(240 * 2).ToArray(), s => Assert.Equal(0f, s));
        Assert.Equal(0, player.ActiveVoices);
    }

    [Fact]
    public void PauseAndResume_ContinueFromTheSamePosition()
    {
        SoundPlayer player = CreatePlayer();
        var id = Guid.NewGuid();
        player.Play(id, Constant(0.5f, 48_000), 0, loop: false);
        Render(player, blocks: 3);

        player.Pause(id);
        Render(player, blocks: 2);
        SoundPlaybackState paused = player.GetState(id);
        float[] whilePaused = Render(player);
        player.Resume(id);
        Render(player, blocks: 2);
        SoundPlaybackState resumed = player.GetState(id);

        Assert.Equal(SoundPlaybackStatus.Paused, paused.Status);
        Assert.All(whilePaused, s => Assert.Equal(0f, s));
        Assert.Equal(SoundPlaybackStatus.Playing, resumed.Status);
        Assert.True(resumed.Position > paused.Position);
        Assert.InRange(paused.Position.TotalMilliseconds, 30, 50);
    }

    [Fact]
    public void PlayingAgain_RestartsInsteadOfStackingTheSameSound()
    {
        SoundPlayer player = CreatePlayer();
        var id = Guid.NewGuid();
        player.Play(id, Constant(0.5f, 48_000), 0, loop: false);
        Render(player, blocks: 10);

        player.Play(id, Constant(0.5f, 48_000), 0, loop: false);
        float[] output = Render(player, blocks: 3);

        Assert.True(output.Max() <= 0.5f + 1e-5f);   // nunca suena dos veces a la vez
        Assert.True(player.GetState(id).Position.TotalMilliseconds < 40);
    }

    [Fact]
    public void ClipInAnotherFormat_IsRejected()
    {
        SoundPlayer player = CreatePlayer();
        var clip = new SoundClip(new float[200], new AudioFormat(44_100, 2), "otro formato");

        player.Play(Guid.NewGuid(), clip, 0, false);
        Render(player);

        Assert.Equal(1, player.RejectedClips);
        Assert.Equal(0, player.ActiveVoices);
    }

    [Fact]
    public void WhenAllVoicesAreBusy_TheOldestIsReused()
    {
        SoundPlayer player = CreatePlayer();
        var first = Guid.NewGuid();
        player.Play(first, Constant(0.01f, 48_000), 0, true);
        Render(player);
        for (int i = 0; i < SoundPlayer.VoiceCount; i++)
        {
            player.Play(Guid.NewGuid(), Constant(0.01f, 48_000), 0, true);
            Render(player);
        }

        Assert.Equal(1, player.StolenVoices);
        Assert.Equal(SoundPlaybackStatus.Stopped, player.GetState(first).Status);
        Assert.Equal(SoundPlayer.VoiceCount, player.ActiveVoices);
    }

    [Fact]
    public void GainCommands_ApplyToTheRunningSound()
    {
        SoundPlayer player = CreatePlayer();
        var id = Guid.NewGuid();
        player.Play(id, Constant(1f, 48_000), 0, false);
        Render(player, blocks: 2);

        player.SetGain(id, -6.0206f);
        Render(player);
        float[] after = Render(player);

        Assert.All(after, s => Assert.Equal(0.5f, s, 1e-4f));
    }
}
