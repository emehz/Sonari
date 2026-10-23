namespace OndaMix.Presentation.ViewModels;

/// <summary>
/// Página cuya funcionalidad todavía no existe. Muestra un estado vacío honesto (qué incluirá y en qué fase)
/// en lugar de controles que no hacen nada.
/// </summary>
public sealed class PlannedPageViewModel(
    string key,
    string title,
    string glyph,
    string subtitle,
    int plannedPhase,
    IReadOnlyList<string> plannedFeatures)
    : PageViewModel(key, title, glyph, subtitle)
{
    public int PlannedPhase { get; } = plannedPhase;

    public string PhaseLabel => $"Planificado para la Fase {PlannedPhase}";

    public IReadOnlyList<string> PlannedFeatures { get; } = plannedFeatures;

    /// <summary>Páginas pendientes, en el orden en que aparecen en la navegación (después del Mezclador).</summary>
    public static IEnumerable<PlannedPageViewModel> CreateDefaults()
    {
        yield return new("sources", "Fuentes", "",
            "Micrófono físico, aplicaciones concretas y audio del sistema.",
            3,
            [
                "Selector de procesos con icono, PID y actividad de audio",
                "Captura por aplicación (Application Loopback)",
                "Audio del sistema excluyendo a OndaMix para evitar realimentación",
                "Reenganche automático cuando una aplicación se reinicia",
            ]);

        yield return new("soundboard", "Sonidos", "",
            "Bancos de sonidos con atajos globales y parada de emergencia.",
            4,
            [
                "Importar WAV, MP3 y otros formatos de Media Foundation",
                "Reproducción simultánea, pausa, bucle y volumen por sonido",
                "Categorías, bancos y atajos de teclado globales",
                "Parada de emergencia de todos los efectos",
            ]);

        yield return new("voice", "Voz", "",
            "Cadena de efectos en tiempo real para el micrófono.",
            6,
            [
                "Puerta de ruido, pasaaltos, ecualizador, compresor y limitador",
                "Cambio de tono, eco, reverberación, distorsión y modulación",
                "Orden configurable y bypass sin clics",
                "Arquitectura preparada para módulos de IA opcionales",
            ]);

        yield return new("scenes", "Escenas", "",
            "Configuraciones completas que se activan con un clic o un atajo.",
            7,
            [
                "Guardar fuentes, volúmenes, efectos y destinos",
                "Cambio de escena sin reiniciar el motor",
                "Atajos globales con detección de conflictos",
            ]);
    }
}
