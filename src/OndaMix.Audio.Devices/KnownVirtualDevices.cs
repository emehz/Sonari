namespace OndaMix.Audio.Devices;

/// <summary>
/// Reconoce por nombre los cables y micrófonos virtuales más comunes. Sirve para orientar al usuario
/// (p. ej. «este parece un cable virtual»), nunca para tomar decisiones de enrutamiento automáticas.
/// </summary>
public static class KnownVirtualDevices
{
    private static readonly string[] NameFragments =
    [
        "VB-Audio",
        "CABLE Input",
        "CABLE Output",
        "VoiceMeeter",
        "Virtual Audio Cable",
        "Virtual Mic",
        "Virtual Speaker",
        "Voicemod",
        "Steam Streaming",
    ];

    public static bool IsLikelyVirtual(string? endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName))
        {
            return false;
        }

        foreach (string fragment in NameFragments)
        {
            if (endpointName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
