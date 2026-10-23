using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Sonari.Presentation.Services;

/// <summary>Acciones que delegan en el shell de Windows. Es una interfaz para poder sustituirla en las pruebas de ViewModels.</summary>
public interface IShellIntegration
{
    void OpenFolder(string path);
}

public sealed class ShellIntegration(ILogger<ShellIntegration> logger) : IShellIntegration
{
    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "open",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "No se pudo abrir la carpeta {Path}", path);
        }
    }
}
