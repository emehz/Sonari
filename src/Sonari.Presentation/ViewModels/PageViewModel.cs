using CommunityToolkit.Mvvm.ComponentModel;

namespace Sonari.Presentation.ViewModels;

/// <summary>Sección de la navegación lateral. Cada página vive mientras vive la ventana y conserva su estado.</summary>
public abstract class PageViewModel(string key, string title, string glyph, string subtitle) : ObservableObject
{
    /// <summary>Identificador estable que se persiste (p. ej. «devices»). No traducir.</summary>
    public string Key { get; } = key;

    public string Title { get; } = title;

    /// <summary>Carácter de Segoe MDL2 Assets / Segoe Fluent Icons.</summary>
    public string Glyph { get; } = glyph;

    public string Subtitle { get; } = subtitle;

    /// <summary>Se llama en el hilo de UI cada vez que la página pasa a ser visible.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
}
