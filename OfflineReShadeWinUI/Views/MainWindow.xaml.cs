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

public sealed partial class MainWindow : Window
{
    private readonly EffectsPanel _effects;
    private readonly PreviewCanvas _canvas;
    private readonly InputPanel _input;
    private readonly DispatcherTimer _sliderModeOverlayTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    private bool _exportDialogOpen, _closePrepared, _closeRequested, _isClosing;
    public MainWindowViewModel ViewModel { get; }
    public MainWindow()
    {
        InitializeComponent();
        Title = "Offline ReShade " + BuildContract.ProductVersion;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
        SystemBackdrop = new MicaBackdrop();
        var windowHandle = WindowNative.GetWindowHandle(this);
        var uiContext = new UiSynchronizationContext(DispatcherQueue);
        SynchronizationContext.SetSynchronizationContext(uiContext);
        ViewModel = new MainWindowViewModel(new AppPaths(), uiContext);
        ViewModel.Initialize(new SettingsPickerService(() => windowHandle));
        _effects = new(ViewModel); _canvas = new(ViewModel); _input = new(ViewModel);
        EffectsHost.Content = _effects; CanvasHost.Content = _canvas; InputHost.Content = _input;
        _effects.SliderModeChanged += ShowSliderModeOverlay;
        ViewModel.ExportRequested += OnExportRequested;
        ViewModel.BatchRequested += OnBatchRequested;
        ViewModel.BatchResultsRequested += OnBatchResultsRequested;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Settings.PropertyChanged += OnSettingsChanged;
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        RootGrid.SizeChanged += (_, _) => ArrangeWorkspace();
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        _sliderModeOverlayTimer.Tick += (_, _) => { _sliderModeOverlayTimer.Stop(); SliderModeOverlay.Visibility = Visibility.Collapsed; };
        AppWindow.Closing += async (_, args) =>
        {
            if (_closePrepared) return;
            args.Cancel = true;
            if (_closeRequested) return;
            _closeRequested = true;
            try
            {
                await ViewModel.PrepareCloseAsync();
                _closePrepared = true;
                Close();
            }
            catch (OperationCanceledException) { _closeRequested = false; }
            catch (Exception ex)
            {
                _closeRequested = false;
                ViewModel.ReportUiError(ex, "close_session");
            }
        };
        ViewModel.UnsavedDecisionRequested = async (preset, canSave) =>
        {
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                Title = "Save preset changes?", Content = "Current FX changes have not been saved.\n" + preset,
                PrimaryButtonText = "Save", IsPrimaryButtonEnabled = canSave, SecondaryButtonText = "Discard", CloseButtonText = "Cancel",
                DefaultButton = canSave ? ContentDialogButton.Primary : ContentDialogButton.Close };
            return await dialog.ShowAsync() switch { ContentDialogResult.Primary => "save", ContentDialogResult.Secondary => "discard", _ => "cancel" };
        };

        Closed += (_, _) =>
        {
            _isClosing = true; _sliderModeOverlayTimer.Stop();
            ViewModel.ExportRequested -= OnExportRequested; ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.BatchRequested -= OnBatchRequested; ViewModel.BatchResultsRequested -= OnBatchResultsRequested;
            ViewModel.Settings.PropertyChanged -= OnSettingsChanged;
            _effects.Dispose(); _canvas.Dispose(); _input.Dispose(); ViewModel.Dispose();
        };
        PreviewTransportBox.SelectedIndex = ViewModel.Settings.PreviewTransport == "CPU" ? 1 : 0;
        ApplyTheme(); UpdateContentView(); UpdateGalleryView();
        var dpi = GetDpiForWindow(windowHandle) / 96.0;
        var workArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(workArea.Width, (int)(1280 * dpi)), Math.Min(workArea.Height, (int)(800 * dpi))));
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_isClosing) return;
        if (args.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen)) UpdateContentView();
        if (args.PropertyName is nameof(MainWindowViewModel.IsGalleryPanelOpen) or nameof(MainWindowViewModel.IsGalleryMode)) UpdateGalleryView();
        if (args.PropertyName == nameof(MainWindowViewModel.SelectedGalleryItem)) SyncGallerySelection();
    }
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_isClosing) return;
        if (args.PropertyName is nameof(SettingsViewModel.EffectsPaneOpen) or nameof(SettingsViewModel.EffectsPaneWidth)) ArrangeWorkspace();
        if (args.PropertyName == nameof(SettingsViewModel.Theme)) ApplyTheme();
    }
    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ViewModel.Settings.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        ThemeBox.SelectedIndex = ViewModel.Settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        UpdateCaptionButtons();
    }
    // Caption buttons are drawn by the system, so they need the window's theme explicitly when it differs from Windows.
    private void UpdateCaptionButtons()
    {
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = bar.ButtonHoverForegroundColor = bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Windows.UI.Color.FromArgb(0xFF, 0x75, 0x75, 0x75);
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x10, 0x00, 0x00, 0x00);
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
    }
    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel != null && ThemeBox.SelectedItem is ComboBoxItem { Tag: string theme }) ViewModel.Settings.Theme = theme;
    }
    private void UpdateContentView()
    {
        MainContent.Visibility = ViewModel.IsSettingsOpen ? Visibility.Collapsed : Visibility.Visible;
        SettingsContent.Visibility = ViewModel.IsSettingsOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnCloseInput(object sender, RoutedEventArgs args) => ViewModel.IsInputPanelOpen = false;
    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs args) => ArrangeWorkspace();
    private void ArrangeWorkspace()
    {
        if (ViewModel == null || _effects == null) return;
        InputSplit.OpenPaneLength = Math.Min(440, Math.Max(0, RootGrid.ActualWidth));
        var layout = WorkspaceLayout.Arrange(Math.Max(0, MainContent.ActualWidth - 24), ViewModel.Settings.EffectsPaneWidth, ViewModel.IsGalleryPanelOpen);
        EffectsSplit.OpenPaneLength = layout.EffectsWidth;
        EffectsSplit.DisplayMode = layout.EffectsInline ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        GallerySplit.DisplayMode = layout.GalleryInline ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        _effects.SetPaneVisible(ViewModel.Settings.EffectsPaneOpen);
    }
    private void UpdateGalleryView() { ArrangeWorkspace(); SyncGallerySelection(); }
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || args.KeyStatus.WasKeyDown || _exportDialogOpen || _closeRequested || IsTextEntryFocused() || _effects.IsNativeFocused) return;
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (control)
        {
            System.Windows.Input.ICommand? command = args.Key switch
            {
                Windows.System.VirtualKey.O => ViewModel.OpenImageCommand,
                Windows.System.VirtualKey.E => ViewModel.OpenExportCommand,
                Windows.System.VirtualKey.Z => ViewModel.UndoCommand,
                Windows.System.VirtualKey.Y => ViewModel.RedoCommand,
                Windows.System.VirtualKey.S => ViewModel.SavePresetCommand,
                _ => null
            };
            if (command?.CanExecute(null) == true) { command.Execute(null); args.Handled = true; return; }
            if (args.Key == Windows.System.VirtualKey.Number0) { _canvas.Fit(); args.Handled = true; }
            if (args.Key == Windows.System.VirtualKey.Number1) { _canvas.ActualPixels(); args.Handled = true; }
            if (args.Key == Windows.System.VirtualKey.B) { _ = RunUiCommandAsync(_canvas.ToggleComparisonAsync); args.Handled = true; }
            if (args.Key == Windows.System.VirtualKey.F) { ViewModel.Settings.EffectsPaneOpen = true; DispatcherQueue.TryEnqueue(_effects.FocusSearch); args.Handled = true; }
        }
        else if (args.Key == Windows.System.VirtualKey.L) { _effects.ToggleLogMode(); args.Handled = true; }
    }
    private void SyncGallerySelection()
    {
        if (GalleryList.SelectedItem != ViewModel.SelectedGalleryItem)
            GalleryList.SelectedItem = ViewModel.SelectedGalleryItem;
    }

    private async void OnGallerySelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (GalleryList.SelectedItem is not GalleryItemViewModel item)
            return;

        if (ReferenceEquals(item, ViewModel.SelectedGalleryItem)) return;
        await RunUiCommandAsync(() => ViewModel.SelectGalleryItemAsync(item));
        SyncGallerySelection();
    }

    private void OnPreviewTransportSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel == null)
            return;

        if (PreviewTransportBox.SelectedItem is ComboBoxItem item && item.Tag is string transport)
        {
            ViewModel.Settings.PreviewTransport = transport;

        }
    }

    private void ShowSliderModeOverlay(string text)
    {
        SliderModeOverlayText.Text = text;
        SliderModeOverlay.Visibility = Visibility.Visible;
        _sliderModeOverlayTimer.Stop();
        _sliderModeOverlayTimer.Start();
    }

    private bool IsTextEntryFocused()
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        return FindVisualAncestor<TextBox>(focused) is not null ||
            FindVisualAncestor<RichEditBox>(focused) is not null ||
            FindVisualAncestor<PasswordBox>(focused) is not null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
        }

        return null;
    }

    private async Task<bool> RunUiCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            ViewModel.ReportUiError(ex);
            return false;
        }
    }
    private async void OnExportRequested()
    {
        if (_exportDialogOpen) return;
        _exportDialogOpen = true;
        try
        {
            await ViewModel.PrepareExportReviewAsync();
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Export image",
                Content = new ExportPanel(ViewModel),
                PrimaryButtonText = ViewModel.ExportActionText,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = ViewModel.CanExport
            };
            System.ComponentModel.PropertyChangedEventHandler changed = (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.ExportActionText)) dialog.PrimaryButtonText = ViewModel.ExportActionText;
                if (args.PropertyName == nameof(MainWindowViewModel.CanExport)) dialog.IsPrimaryButtonEnabled = ViewModel.CanExport;
            };
            ViewModel.PropertyChanged += changed;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                args.Cancel = true; // Keep results, warnings and file-opening actions visible.
                try { await RunUiCommandAsync(ViewModel.ExportImageAsync); }
                finally { deferral.Complete(); }
            };
            try { await dialog.ShowAsync(); }
            finally { ViewModel.PropertyChanged -= changed; }
        }
        catch (Exception error) { ViewModel.ReportUiError(error); }
        finally { _exportDialogOpen = false; }
    }
    private async void OnBatchRequested()
    {
        if (_exportDialogOpen) return;
        _exportDialogOpen = true;
        var started = false;
        try
        {
            var model = await ViewModel.PrepareBatchSetupAsync();
            var panel = new BatchSetupPanel(ViewModel, model);
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                Title = "Batch export", Content = panel, PrimaryButtonText = "Start batch", CloseButtonText = "Cancel",
                IsPrimaryButtonEnabled = model.CanStart, DefaultButton = ContentDialogButton.Primary };
            System.ComponentModel.PropertyChangedEventHandler changed = (_, _) => dialog.IsPrimaryButtonEnabled = model.CanStart && ViewModel.CanEditSession;
            model.PropertyChanged += changed; ViewModel.PropertyChanged += changed;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try { await ViewModel.StartReviewedBatchAsync(model.Request ?? throw new InvalidOperationException("Review the batch inputs.")); started = true; }
                catch (Exception error) { args.Cancel = true; panel.ShowError(error.Message); ViewModel.ReportUiError(error, "batch_start"); }
                finally { deferral.Complete(); }
            };
            try { await dialog.ShowAsync(); }
            finally { model.PropertyChanged -= changed; ViewModel.PropertyChanged -= changed; }
        }
        catch (Exception error) { ViewModel.ReportUiError(error, "batch_preflight"); }
        finally { _exportDialogOpen = false; }
        if (started) OnBatchResultsRequested();
    }
    private async void OnBatchResultsRequested()
    {
        if (_exportDialogOpen) return;
        _exportDialogOpen = true;
        try
        {
            await new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme,
                Title = "Batch results", Content = new BatchResultsPanel(ViewModel), CloseButtonText = "Close" }.ShowAsync();
        }
        catch (Exception error) { ViewModel.ReportUiError(error); }
        finally { _exportDialogOpen = false; }
    }
    private async void OnShowShortcuts(object sender, RoutedEventArgs args)
    {
        if (_exportDialogOpen) return;
        var shortcuts = new (string Keys, string Action)[]
        {
            ("Ctrl+O", "Open image"), ("Ctrl+E", "Export image"), ("Ctrl+S", "Save preset"),
            ("Ctrl+Z / Ctrl+Y", "Undo / redo"), ("Ctrl+F", "Search effects and parameters"),
            ("Ctrl+0 / Ctrl+1", "Fit / 100%"), ("Ctrl+B", "Compare with the original image"),
            ("Arrow keys", "Pan the canvas"), ("L", "Toggle logarithmic sliders"),
            ("Ctrl + wheel on a slider", "Change slider sensitivity")
        };
        var table = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < shortcuts.Length; i++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = shortcuts[i].Keys, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var action = new TextBlock { Text = shortcuts[i].Action, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i); Grid.SetRow(action, i); Grid.SetColumn(action, 1);
            table.Children.Add(keys); table.Children.Add(action);
        }
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(table);
        content.Children.Add(new TextBlock { Text = "Text fields keep their normal editing shortcuts.", Style = (Style)Application.Current.Resources["SecondaryTextStyle"], TextWrapping = TextWrapping.Wrap });
        _exportDialogOpen = true;
        try
        {
            await new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = "Keyboard shortcuts",
                Content = content, CloseButtonText = "Close", DefaultButton = ContentDialogButton.Close }.ShowAsync();
        }
        catch (Exception error) { ViewModel.ReportUiError(error); }
        finally { _exportDialogOpen = false; }
    }
    public static Visibility BoolVisibility(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility ToneVisibility(string tone, string expected) => BoolVisibility(tone == expected);
    public static InfoBarSeverity NoticeSeverity(string status) => status is "failed" or "rejected" ? InfoBarSeverity.Error : status == "warning" ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
}
