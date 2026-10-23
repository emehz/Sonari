namespace Sonari.Presentation.ViewModels;

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

    /// <summary>Páginas pendientes, en el orden en que aparecen en la navegación (después de Mezclador, Fuentes y Sonidos).</summary>
    public static IEnumerable<PlannedPageViewModel> CreateDefaults()
    {
        yield return new("voice", "Voz", "\uE720",
            "Cadena de efectos en tiempo real para el micrófono.",
            6,
            [
                "Puerta de ruido, pasaaltos, ecualizador, compresor y limitador",
                "Cambio de tono, eco, reverberación, distorsión y modulación",
                "Orden configurable y bypass sin clics",
                "Arquitectura preparada para módulos de IA opcionales",
            ]);

        yield return new("scenes", "Escenas", "\uE8A9",
            "Configuraciones completas que se activan con un clic o un atajo.",
            7,
            [
                "Guardar fuentes, volúmenes, efectos y destinos",
                "Cambio de escena sin reiniciar el motor",
                "Atajos globales con detección de conflictos",
            ]);
    }
}
