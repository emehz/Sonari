namespace Sonari.Audio.Core.Devices;

/// <summary>
/// Relaciona los dos extremos de un cable virtual por su nombre. Windows nombra los endpoints como
/// «Función (Dispositivo)»: la entrada «CABLE Input (VB-Audio Virtual Cable)» y la salida
/// «CABLE Output (VB-Audio Virtual Cable)» comparten el dispositivo entre paréntesis.
/// Es una heurística: sirve para orientar y prevenir errores, no para decidir rutas por su cuenta.
/// </summary>
public static class VirtualCablePairing
{
    /// <summary>
    /// Texto del último paréntesis (el dispositivo), o el nombre completo si no lo hay. Respeta paréntesis anidados,
    /// como en «Altavoces (Realtek(R) Audio)».
    /// </summary>
    public static string DeviceFamily(string endpointName)
    {
        ArgumentNullException.ThrowIfNull(endpointName);
        int close = endpointName.LastIndexOf(')');
        if (close < 0)
        {
            return endpointName.Trim();
        }

        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (endpointName[i] == ')')
            {
                depth++;
            }
            else if (endpointName[i] == '(' && --depth == 0)
            {
                return endpointName[(i + 1)..close].Trim();
            }
        }

        return endpointName.Trim();
    }

    /// <summary>
    /// Endpoint de captura que recibe lo que se escribe en <paramref name="render"/>: el «micrófono» que hay que elegir
    /// en Discord. Solo se busca si la salida parece un cable virtual.
    /// </summary>
    public static AudioEndpointInfo? FindCaptureSide(AudioEndpointInfo render, IEnumerable<AudioEndpointInfo> captures)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(captures);
        if (!render.IsLikelyVirtual)
        {
            return null;
        }

        string family = DeviceFamily(render.Name);
        return captures.FirstOrDefault(c =>
            c.IsLikelyVirtual && string.Equals(DeviceFamily(c.Name), family, StringComparison.OrdinalIgnoreCase));
    }
}
