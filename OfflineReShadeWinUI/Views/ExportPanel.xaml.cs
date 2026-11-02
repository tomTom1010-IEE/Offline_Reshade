using Microsoft.UI.Xaml.Controls;
using OfflineReShade.WinUI.ViewModels;

namespace OfflineReShade.WinUI.Views;

public sealed partial class ExportPanel : UserControl
{
    public MainWindowViewModel ViewModel { get; }
    public ExportPanel(MainWindowViewModel viewModel) { ViewModel = viewModel; InitializeComponent(); }
}
