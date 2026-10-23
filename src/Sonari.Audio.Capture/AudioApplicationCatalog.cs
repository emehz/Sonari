using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Sonari.Audio.Capture.Processes;
using Sonari.Audio.Core.Applications;

namespace Sonari.Audio.Capture;

/// <summary>
/// Lista las aplicaciones que tienen audio: lee las sesiones de audio de cada salida activa (PID, estado y nivel de
/// pico que Windows ya mide por sesión) y las agrupa por proceso raíz. Solo lee; no captura nada.
/// </summary>
public sealed class AudioApplicationCatalog(ILogger<AudioApplicationCatalog> logger) : IAudioApplicationCatalog
{
    // Ruta y descripción no cambian durante la vida de un proceso: se leen una vez por PID.
    private readonly Dictionary<int, (string? Path, string Description)> _identityCache = [];
    private readonly object _cacheLock = new();

    public IReadOnlyList<AudioApplicationInfo> GetApplications(bool includeWithoutAudio)
    {
        Dictionary<int, ProcessEntry> snapshot = ProcessNative.TakeSnapshot();
        var rootOf = new Dictionary<int, int>(snapshot.Count);
        foreach (int pid in snapshot.Keys)
        {
            rootOf[pid] = ProcessTree.FindRoot(snapshot, pid);
        }

        int ownRoot = rootOf.GetValueOrDefault(Environment.ProcessId, Environment.ProcessId);
        var groups = new Dictionary<int, SessionSummary>();

        CollectSessions(rootOf, ownRoot, groups);
        if (includeWithoutAudio)
        {
            CollectWindowedProcesses(rootOf, ownRoot, groups);
        }

        var result = new List<AudioApplicationInfo>(groups.Count);
        foreach ((int root, SessionSummary summary) in groups)
        {
            if (!snapshot.TryGetValue(root, out ProcessEntry entry))
            {
                continue;
            }

            (string? path, string description) = GetIdentity(root, entry.ExeName);
            result.Add(new AudioApplicationInfo(
                root,
                entry.ExeName,
                description,
                path,
                TryGetWindowTitle(root),
                summary.HasSession,
                summary.IsPlaying,
                summary.Peak,
                rootOf.Count(pair => pair.Value == root)));
        }

        PruneCache(snapshot);

        return result
            .OrderByDescending(a => a.IsPlaying)
            .ThenByDescending(a => a.HasAudioSession)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void CollectSessions(Dictionary<int, int> rootOf, int ownRoot, Dictionary<int, SessionSummary> groups)
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (MMDevice device in devices)
        {
            using (device)
            {
                try
                {
                    SessionCollection sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using AudioSessionControl session = sessions[i];
                        if (session.IsSystemSoundsSession)
                        {
                            continue;
                        }

                        int pid = (int)session.GetProcessID;
                        int root = rootOf.GetValueOrDefault(pid, pid);
                        if (pid == 0 || root == ownRoot)
                        {
                            continue;
                        }

                        SessionSummary summary = groups.GetValueOrDefault(root);
                        summary.HasSession = true;
                        if (session.State == AudioSessionState.AudioSessionStateActive)
                        {
                            summary.IsPlaying = true;
                            summary.Peak = MathF.Max(summary.Peak, TryGetPeak(session));
                        }

                        groups[root] = summary;
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    // Un dispositivo que desaparece a mitad de la lectura no debe vaciar toda la lista.
                    logger.LogDebug(ex, "No se pudieron leer las sesiones de {Device}", device.ID);
                }
            }
        }
    }

    private static void CollectWindowedProcesses(Dictionary<int, int> rootOf, int ownRoot, Dictionary<int, SessionSummary> groups)
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        continue;
                    }
                }
                catch (InvalidOperationException)
                {
                    continue;   // terminó mientras se leía
                }

                int root = rootOf.GetValueOrDefault(process.Id, process.Id);
                if (root != ownRoot && !groups.ContainsKey(root))
                {
                    groups[root] = default;
                }
            }
        }
    }

    private (string? Path, string Description) GetIdentity(int processId, string exeName)
    {
        lock (_cacheLock)
        {
            if (_identityCache.TryGetValue(processId, out var cached))
            {
                return cached;
            }
        }

        string? path = ProcessNative.TryGetExecutablePath(processId);
        string description = Path.GetFileNameWithoutExtension(exeName);
        if (path is not null)
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                string? fromFile = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                if (!string.IsNullOrWhiteSpace(fromFile))
                {
                    description = fromFile.Trim();
                }
            }
            catch (FileNotFoundException)
            {
                // El ejecutable se borró o se movió con el proceso en marcha: se queda el nombre del archivo.
            }
        }

        lock (_cacheLock)
        {
            _identityCache[processId] = (path, description);
        }

        return (path, description);
    }

    private static string? TryGetWindowTitle(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            string title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static float TryGetPeak(AudioSessionControl session)
    {
        try
        {
            return session.AudioMeterInformation.MasterPeakValue;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException)
        {
            return 0f;
        }
    }

    private void PruneCache(Dictionary<int, ProcessEntry> snapshot)
    {
        lock (_cacheLock)
        {
            foreach (int pid in _identityCache.Keys.Where(pid => !snapshot.ContainsKey(pid)).ToList())
            {
                _identityCache.Remove(pid);
            }
        }
    }

    private struct SessionSummary
    {
        public bool HasSession;
        public bool IsPlaying;
        public float Peak;
    }
}
