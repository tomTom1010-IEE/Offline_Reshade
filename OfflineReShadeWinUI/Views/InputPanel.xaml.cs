using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using System.Globalization;
using OfflineReShade.Core;
using OfflineReShade.WinUI.Controls;
using OfflineReShade.WinUI.Services;
using OfflineReShade.WinUI.ViewModels;
using WinRT.Interop;

namespace OfflineReShade.WinUI.Views;

public sealed partial class InputPanel : UserControl, IDisposable
{
    public MainWindowViewModel ViewModel { get; }
    public InputPanel(MainWindowViewModel vm)
    {
        ViewModel = vm; InitializeComponent();
        ViewModel.Settings.PropertyChanged += OnSettingsChanged;
        RefreshSelections();
    }
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.DepthFormat) or nameof(SettingsViewModel.DepthProfile) or nameof(SettingsViewModel.DepthDownsample)) RefreshSelections();
    }
    private void RefreshSelections()
    {
        UpdateDepthProfileSelection(); UpdateDepthFormatSelection(); UpdateDepthDownsampleSelection();
    }
    public void SetAvailableHeight(double height) => InputPanelScroll.MaxHeight = Math.Clamp(height * .4, 180, 340);
    public void Dispose() => ViewModel.Settings.PropertyChanged -= OnSettingsChanged;
    private void OnDepthFormatSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel == null)
            return;

        if (string.Equals(ViewModel.Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.Settings.DepthFormat = "raw";
            UpdateDepthFormatSelection();
            return;
        }

        if (DepthFormatBox.SelectedItem is ComboBoxItem item && item.Tag is string format)
        {
            var oldFormat = ViewModel.Settings.DepthFormat;
            ViewModel.Settings.DepthFormat = format;
            UpdateDepthPathForFormat(oldFormat, format);
        }
    }

    private void OnDepthProfileSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel == null)
            return;

        if (DepthProfileBox.SelectedItem is ComboBoxItem item && item.Tag is string profile)
            ViewModel.Settings.DepthProfile = profile;
    }

    private void OnDepthDownsampleSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel == null)
            return;

        if (DepthDownsampleBox.SelectedItem is ComboBoxItem item && item.Tag is string filter)
            ViewModel.Settings.DepthDownsample = filter;
    }

    private void UpdateDepthFormatSelection()
    {
        var selectedIndex = string.Equals(ViewModel.Settings.DepthFormat, "rgba", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (DepthFormatBox.SelectedIndex != selectedIndex)
            DepthFormatBox.SelectedIndex = selectedIndex;
        UpdateDepthFormatAvailability();
    }

    private void UpdateDepthFormatAvailability()
    {
        DepthFormatBox.IsEnabled = !string.Equals(ViewModel.Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDepthProfileSelection()
    {
        var selectedIndex = string.Equals(ViewModel.Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (DepthProfileBox.SelectedIndex != selectedIndex)
            DepthProfileBox.SelectedIndex = selectedIndex;
    }

    private void UpdateDepthDownsampleSelection()
    {
        var selectedIndex = ViewModel.Settings.DepthDownsample?.ToLowerInvariant() switch
        {
            "box" => 1,
            _ => 0
        };
        if (DepthDownsampleBox.SelectedIndex != selectedIndex)
            DepthDownsampleBox.SelectedIndex = selectedIndex;
        UpdateDepthDownsampleAvailability();
    }

    private void UpdateDepthDownsampleAvailability()
    {
        DepthDownsampleBox.IsEnabled = string.Equals(ViewModel.Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDepthPathForFormat(string oldFormat, string newFormat)
    {
        if (string.Equals(oldFormat, newFormat, StringComparison.OrdinalIgnoreCase))
            return;

        var path = ViewModel.Settings.DepthPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fileName = System.IO.Path.GetFileName(path);
        if (string.Equals(newFormat, "raw", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(fileName, "depthoutput.png", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Settings.DepthPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? string.Empty, "depthoutput.rfloat");
        }
        else if (string.Equals(newFormat, "rgba", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(fileName, "depthoutput.rfloat", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rfloat", StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Settings.DepthPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? string.Empty, "depthoutput.png");
        }
    }

}
