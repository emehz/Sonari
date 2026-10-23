using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Sonari.Infrastructure.Storage;

/// <summary>
/// Lectura y escritura de documentos JSON con dos garantías:
/// <list type="bullet">
/// <item>Escritura atómica: se escribe un archivo temporal, se vuelca a disco y se sustituye el original. Un corte de luz
/// deja el archivo anterior o el nuevo, nunca uno a medias.</item>
/// <item>Recuperación: un archivo ilegible se aparta como <c>*.corrupt-AAAAMMDD-HHMMSS.json</c> y se devuelve <c>null</c>
/// para que el llamador use valores por defecto.</item>
/// </list>
/// </summary>
public sealed class JsonFileStore(ILogger<JsonFileStore> logger)
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public T? TryLoad<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, SerializerOptions);
        }
        catch (JsonException ex)
        {
            string quarantined = Quarantine(path);
            logger.LogWarning(ex, "Archivo JSON corrupto; apartado como {Quarantined}", quarantined);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "No se pudo leer {Path}", path);
            return null;
        }
    }

    public async Task SaveAsync<T>(string path, T document, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("La ruta no tiene carpeta.", nameof(path));
        Directory.CreateDirectory(directory);

        string tempPath = path + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static string Quarantine(string path)
    {
        string target = Path.ChangeExtension(path, null) + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        File.Move(path, target, overwrite: true);
        return target;
    }
}
