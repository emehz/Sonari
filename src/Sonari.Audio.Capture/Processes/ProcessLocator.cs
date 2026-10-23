using Sonari.Audio.Core.Endpoints;

namespace Sonari.Audio.Capture.Processes;

public sealed class ProcessLocator : IProcessLocator
{
    public int CurrentProcessId { get; } = Environment.ProcessId;

    public int? FindRootProcess(string executablePath, int? preferredProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        Dictionary<int, ProcessEntry> snapshot = ProcessNative.TakeSnapshot();
        string fileName = Path.GetFileName(executablePath);
        var roots = new List<int>();

        foreach (ProcessEntry entry in snapshot.Values)
        {
            if (entry.ProcessId == CurrentProcessId ||
                !string.Equals(entry.ExeName, fileName, StringComparison.OrdinalIgnoreCase) ||
                ProcessTree.FindRoot(snapshot, entry.ProcessId) != entry.ProcessId)
            {
                continue;
            }

            // Mismo nombre no implica misma aplicación (dos instalaciones distintas). Si no se puede leer la ruta
            // (proceso protegido), se acepta por nombre.
            string? path = ProcessNative.TryGetExecutablePath(entry.ProcessId);
            if (path is not null && !string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            roots.Add(entry.ProcessId);
        }

        if (preferredProcessId is { } preferred && roots.Contains(preferred))
        {
            return preferred;
        }

        return roots.Count > 0 ? roots.Min() : null;
    }

    public bool IsRunning(int processId) => ProcessNative.IsRunning(processId);
}
