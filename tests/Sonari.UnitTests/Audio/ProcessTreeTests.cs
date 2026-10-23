using Sonari.Audio.Capture.Processes;

namespace Sonari.UnitTests.Audio;

public class ProcessTreeTests
{
    private static Dictionary<int, ProcessEntry> Snapshot(params (int Pid, int Parent, string Exe)[] processes) =>
        processes.ToDictionary(p => p.Pid, p => new ProcessEntry(p.Pid, p.Parent, p.Exe));

    [Fact]
    public void ChildWithSameExecutable_GroupsUnderTheTopmostAncestor()
    {
        // explorer → chrome (principal) → chrome (renderer) → chrome (servicio de audio)
        var processes = Snapshot(
            (10, 1, "explorer.exe"),
            (100, 10, "chrome.exe"),
            (110, 100, "chrome.exe"),
            (120, 110, "chrome.exe"));

        Assert.Equal(100, ProcessTree.FindRoot(processes, 120));
        Assert.Equal(100, ProcessTree.FindRoot(processes, 100));
    }

    [Fact]
    public void ParentWithADifferentExecutable_IsNotPartOfTheApplication()
    {
        var processes = Snapshot((10, 1, "explorer.exe"), (200, 10, "Spotify.exe"));

        Assert.Equal(200, ProcessTree.FindRoot(processes, 200));
    }

    [Fact]
    public void ExecutableComparison_IgnoresCase()
    {
        var processes = Snapshot((300, 1, "Discord.exe"), (310, 300, "discord.EXE"));

        Assert.Equal(300, ProcessTree.FindRoot(processes, 310));
    }

    [Fact]
    public void ReusedParentIdForming_ACycle_DoesNotLoopForever()
    {
        // PIDs reutilizados pueden formar un ciclo aparente padre ↔ hijo.
        var processes = Snapshot((400, 401, "app.exe"), (401, 400, "app.exe"));

        int root = ProcessTree.FindRoot(processes, 400);

        Assert.Contains(root, new[] { 400, 401 });
    }

    [Fact]
    public void UnknownProcess_IsItsOwnRoot()
    {
        Assert.Equal(999, ProcessTree.FindRoot(Snapshot(), 999));
    }
}
