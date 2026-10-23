namespace Sonari.Infrastructure.Storage;

/// <summary>
/// Ubicaciones en disco. La configuración va en %APPDATA% (perfil itinerante: viaja con el usuario) y los logs en
/// %LOCALAPPDATA% (no tiene sentido sincronizarlos).
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "Sonari";

    public AppPaths(string configDirectory, string logDirectory)
    {
        ConfigDirectory = configDirectory;
        LogDirectory = logDirectory;
    }

    public string ConfigDirectory { get; }

    public string LogDirectory { get; }

    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    public string SoundboardFile => Path.Combine(ConfigDirectory, "soundboard.json");

    public static AppPaths ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "logs"));

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
