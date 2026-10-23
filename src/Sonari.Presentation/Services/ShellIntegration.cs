using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Sonari.Presentation.Services;

/// <summary>
/// Acciones que delegan en el shell de Windows (carpetas, diálogos). Es una interfaz para que los ViewModels no
/// muestren ventanas directamente y se puedan probar.
/// </summary>
public interface IShellIntegration
{
    void OpenFolder(string path);

    /// <summary>Diálogo «Abrir» de archivos de audio con selección múltiple. Lista vacía si se cancela.</summary>
    IReadOnlyList<string> PickAudioFiles(IEnumerable<string> extensions);

    /// <summary>Pregunta de sí/no. Devuelve <c>true</c> si el usuario acepta.</summary>
    bool Confirm(string message, string title);
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

    public IReadOnlyList<string> PickAudioFiles(IEnumerable<string> extensions)
    {
        string patterns = string.Join(";", extensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = "Importar sonidos",
            Filter = $"Archivos de audio ({patterns})|{patterns}|Todos los archivos (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileNames : [];
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes;
}
