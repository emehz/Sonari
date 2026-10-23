using System.Windows;
using OndaMix.Presentation.Interop;
using OndaMix.Presentation.ViewModels;

namespace OndaMix.Presentation.Views;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }
}
