using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OndaMix.Audio.Core.Devices;

namespace OndaMix.Presentation.ViewModels;

/// <summary>Muestra los endpoints reales y lo que la versión de Windows permite. Solo lee; no abre ningún stream.</summary>
public sealed partial class DevicesViewModel : PageViewModel
{
    private readonly IAudioDeviceCatalog _catalog;
    private readonly ILogger<DevicesViewModel> _logger;
    private bool _loadedOnce;

    [ObservableProperty]
    private AudioSystemCapabilities? _capabilities;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VirtualDeviceSummary))]
    private bool _hasVirtualDevice;

    public DevicesViewModel(IAudioDeviceCatalog catalog, ILogger<DevicesViewModel> logger)
        : base("devices", "Dispositivos", "",
            "Entradas y salidas de audio detectadas en Windows y compatibilidad del sistema.")
    {
        _catalog = catalog;
        _logger = logger;
    }

    public ObservableCollection<AudioEndpointInfo> Inputs { get; } = [];

    public ObservableCollection<AudioEndpointInfo> Outputs { get; } = [];

    public string VirtualDeviceSummary => HasVirtualDevice
        ? "Hay un cable virtual de terceros instalado (marcado abajo). OndaMix lo usará como salida en la Fase 5."
        : "No se detectó ningún cable virtual. Hará falta uno (p. ej. VB-CABLE) en la Fase 5: " +
          "OndaMix todavía no instala su propio driver.";

    public override async Task OnNavigatedToAsync()
    {
        if (!_loadedOnce)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            // La API MMDevice es COM y puede tardar: siempre fuera del hilo de UI.
            var (inputs, outputs, capabilities) = await Task.Run(() => (
                _catalog.GetEndpoints(EndpointFlow.Capture),
                _catalog.GetEndpoints(EndpointFlow.Render),
                _catalog.GetCapabilities()));

            Replace(Inputs, inputs);
            Replace(Outputs, outputs);
            Capabilities = capabilities;
            HasVirtualDevice = inputs.Any(e => e.IsLikelyVirtual) || outputs.Any(e => e.IsLikelyVirtual);
            _loadedOnce = true;
        }
        catch (Exception ex)
        {
            // Un fallo de enumeración no debe tumbar la aplicación: se informa y se puede reintentar.
            _logger.LogError(ex, "Error al enumerar dispositivos de audio");
            ErrorMessage = $"No se pudieron leer los dispositivos de audio: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void Replace(ObservableCollection<AudioEndpointInfo> target, IEnumerable<AudioEndpointInfo> items)
    {
        target.Clear();
        foreach (AudioEndpointInfo item in items)
        {
            target.Add(item);
        }
    }
}
