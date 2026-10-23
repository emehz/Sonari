namespace Sonari.Audio.Capture.Processes;

/// <summary>Reglas para agrupar procesos de una misma aplicación. Lógica pura, sin llamadas al sistema.</summary>
internal static class ProcessTree
{
    private const int MaxDepth = 64;

    /// <summary>
    /// Sube por los padres mientras tengan el mismo ejecutable. Así, el proceso de audio de Chrome o de Discord se
    /// agrupa con su proceso principal, que es el que se captura con «incluir árbol de procesos».
    /// </summary>
    /// <remarks>
    /// Windows reutiliza PIDs: un «padre» con otro ejecutable puede ser un proceso nuevo que heredó el número. Por eso solo
    /// se sube si el ejecutable coincide, y se corta ante ciclos.
    /// </remarks>
    public static int FindRoot(IReadOnlyDictionary<int, ProcessEntry> processes, int processId)
    {
        if (!processes.TryGetValue(processId, out ProcessEntry current))
        {
            return processId;
        }

        var visited = new HashSet<int> { processId };
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (!processes.TryGetValue(current.ParentProcessId, out ProcessEntry parent) ||
                !visited.Add(parent.ProcessId) ||
                !string.Equals(parent.ExeName, current.ExeName, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return current.ProcessId;
    }
}
