namespace Sonari.Audio.Engine.Routing;

/// <summary>Las dos mezclas del motor. Cada una la marca el reloj de su propio dispositivo de salida.</summary>
internal enum BusKind
{
    /// <summary>Lo que oyes tú por los auriculares.</summary>
    Monitor = 0,

    /// <summary>Lo que oyen los demás a través del micrófono virtual.</summary>
    Virtual = 1,
}
