using Microsoft.Extensions.Logging;
using Sonari.Audio.Core.Formats;
using Sonari.Audio.Core.Playback;

namespace Sonari.Audio.Engine.Playback;

/// <summary>
/// Mantiene en memoria los sonidos ya decodificados para que reproducirlos sea instantáneo. Cada archivo se decodifica
/// una sola vez por formato (aunque se pida varias veces a la vez) y se vuelve a leer si cambia en disco. Si se supera
/// el presupuesto de memoria, se descartan los menos usados.
/// </summary>
public sealed class SoundClipCache(ISoundDecoder decoder, ILogger<SoundClipCache> logger) : ISoundClipProvider
{
    /// <summary>≈ 4 sonidos de 5 minutos, o cientos de efectos cortos.</summary>
    public const long MemoryBudgetBytes = 512L * 1024 * 1024;

    private readonly Dictionary<CacheKey, Entry> _entries = [];
    private readonly object _lock = new();
    private long _accessCounter;

    public IReadOnlyCollection<string> SupportedExtensions => decoder.SupportedExtensions;

    /// <exception cref="SoundDecodeException"/>
    public Task<SoundClip> GetAsync(string path, AudioFormat format, CancellationToken cancellationToken = default)
    {
        CacheKey key = new(Path.GetFullPath(path).ToUpperInvariant(), format, GetStamp(path));
        Task<SoundClip> task;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out Entry? existing) && !existing.Task.IsFaulted && !existing.Task.IsCanceled)
            {
                existing.LastAccess = ++_accessCounter;
                return existing.Task;
            }

            task = Task.Run(() => decoder.Decode(path, format, cancellationToken), cancellationToken);
            _entries[key] = new Entry(task) { LastAccess = ++_accessCounter };
        }

        _ = task.ContinueWith(t => OnDecoded(key, t), TaskScheduler.Default);
        return task;
    }

    /// <summary>Olvida un archivo (p. ej. al quitarlo del panel).</summary>
    public void Forget(string path)
    {
        string normalized = Path.GetFullPath(path).ToUpperInvariant();
        lock (_lock)
        {
            foreach (CacheKey key in _entries.Keys.Where(k => k.Path == normalized).ToList())
            {
                _entries.Remove(key);
            }
        }
    }

    private void OnDecoded(CacheKey key, Task<SoundClip> task)
    {
        if (task.IsFaulted)
        {
            logger.LogWarning(task.Exception?.GetBaseException(), "No se pudo decodificar {Path}", key.Path);
            return;
        }

        if (!task.IsCompletedSuccessfully)
        {
            return;
        }

        lock (_lock)
        {
            long total = _entries.Values.Where(e => e.Task.IsCompletedSuccessfully).Sum(e => e.Task.Result.SizeInBytes);
            foreach ((CacheKey oldKey, Entry _) in _entries
                         .Where(e => e.Value.Task.IsCompletedSuccessfully && !e.Key.Equals(key))
                         .OrderBy(e => e.Value.LastAccess)
                         .ToList())
            {
                if (total <= MemoryBudgetBytes)
                {
                    break;
                }

                total -= _entries[oldKey].Task.Result.SizeInBytes;
                _entries.Remove(oldKey);
                logger.LogDebug("Caché de sonidos llena; se descarta {Path}", oldKey.Path);
            }
        }
    }

    private static DateTime GetStamp(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private readonly record struct CacheKey(string Path, AudioFormat Format, DateTime Stamp);

    private sealed class Entry(Task<SoundClip> task)
    {
        public Task<SoundClip> Task { get; } = task;

        public long LastAccess { get; set; }
    }
}
