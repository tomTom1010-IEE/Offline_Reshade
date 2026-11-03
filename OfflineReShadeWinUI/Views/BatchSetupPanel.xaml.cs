using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OfflineReShade.WinUI.ViewModels;

namespace OfflineReShade.WinUI.Views;
public sealed partial class BatchSetupPanel : UserControl
{
    private readonly MainWindowViewModel _owner;
    public BatchSetupViewModel ViewModel { get; }
    public BatchSetupPanel(MainWindowViewModel owner, BatchSetupViewModel model) { _owner = owner; ViewModel = model; InitializeComponent(); }
    public void ShowError(string message) => FailureText.Text = message;
    private async void OnBrowseFolder(object sender, RoutedEventArgs args)
    {
        try { if (await _owner.PickBatchFolderAsync() is { } folder) ViewModel.Folder = folder; }
        catch (Exception error) { ShowError(error.Message); _owner.ReportUiError(error); }
    }
}
