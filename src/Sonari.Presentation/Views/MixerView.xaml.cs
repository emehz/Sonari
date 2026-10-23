using System.Windows.Controls;
using System.Windows.Input;

namespace Sonari.Presentation.Views;

public partial class MixerView : UserControl
{
    public MixerView()
    {
        InitializeComponent();
    }

    // Comportamiento puramente visual (como en una mesa de mezclas): doble clic devuelve el balance al centro.
    private void OnBalanceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            slider.Value = 0;
            e.Handled = true;
        }
    }
}
