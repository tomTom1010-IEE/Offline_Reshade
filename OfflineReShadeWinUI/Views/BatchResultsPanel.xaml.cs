using Microsoft.UI.Xaml.Controls;
using OfflineReShade.WinUI.ViewModels;
namespace OfflineReShade.WinUI.Views;
public sealed partial class BatchResultsPanel : UserControl
{
    public MainWindowViewModel ViewModel { get; }
    public BatchResultsPanel(MainWindowViewModel model) { ViewModel = model; InitializeComponent(); }
}
