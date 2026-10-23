namespace OndaMix.Audio.Core.Devices;

public enum FeatureSupport
{
    Unavailable,

    /// <summary>La versión de Windows debería admitirlo, pero hay que confirmarlo en tiempo de ejecución.</summary>
    Probable,

    Supported,
}

/// <summary>Capacidades de audio que dependen de la versión de Windows.</summary>
public sealed record AudioSystemCapabilities(Version OsVersion, FeatureSupport ProcessLoopback, string ProcessLoopbackDetail)
{
    /// <summary>Windows 10 2004: primera versión en la que el loopback por proceso funciona en la práctica (NAudio, OBS).</summary>
    public const int ProcessLoopbackFirstWorkingBuild = 19041;

    /// <summary>Build mínima según la documentación oficial de AUDIOCLIENT_ACTIVATION_PARAMS.</summary>
    public const int ProcessLoopbackDocumentedBuild = 20348;

    public static AudioSystemCapabilities FromOsVersion(Version osVersion)
    {
        ArgumentNullException.ThrowIfNull(osVersion);

        int build = osVersion.Build;

        if (osVersion.Major < 10 || build < ProcessLoopbackFirstWorkingBuild)
        {
            return new(osVersion, FeatureSupport.Unavailable,
                "Requiere Windows 10 versión 2004 (build 19041) o posterior.");
        }

        if (build < ProcessLoopbackDocumentedBuild)
        {
            return new(osVersion, FeatureSupport.Probable,
                $"Build {build}: funciona en la práctica desde la 19041, aunque la documentación oficial indica la 20348. " +
                "Se confirmará al iniciar la primera captura.");
        }

        return new(osVersion, FeatureSupport.Supported, $"Build {build}: soportado oficialmente.");
    }
}
