using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using System.Globalization;
using OfflineReShade.WinUI.Controls;
using OfflineReShade.WinUI.Services;
using OfflineReShade.WinUI.ViewModels;
using WinRT.Interop;

namespace OfflineReShade.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, CancellationTokenSource> _uniformUpdateSources = new();
    private readonly Dictionary<string, CancellationTokenSource> _addonControlUpdateSources = new();
    private readonly Dictionary<string, Action<AddonImGuiControlViewModel>> _addonControlValueUpdaters = new();
    private readonly List<MappedSlider> _fxMappedSliders = new();
    private readonly List<MappedSlider> _addonMappedSliders = new();
    private readonly List<FrameworkElement> _fxLogIndicators = new();
    private readonly List<FrameworkElement> _addonLogIndicators = new();
    private readonly PreviewHostService _previewHost;
    private readonly D3DPreviewBridge _d3dPreview = new();
    private readonly D3DPreviewBridge _addonOverlayPreview = new();
    private readonly DispatcherTimer _gpuPreviewTimer = new();
    private readonly DispatcherTimer _addonOverlayPreviewTimer = new();
    private readonly DispatcherTimer _addonInputMoveTimer = new();
    private readonly DispatcherTimer _sliderModeOverlayTimer = new();
    private bool _isPreviewDragging;
    private bool _updatingInputModeSwitch;
    private bool _buildingAddonOverlayTabs;
    private bool _updatingAddonControlValues;
    private string _activeNativeAddonOverlayId = string.Empty;
    private string? _draggedEffectName;
    private Point _lastPreviewDragPoint;
    private PreviewHostInputMessage? _pendingAddonMouseMove;
    private uint? _capturedAddonPointerId;
    private uint _lastRequestedAddonOverlayWidth;
    private uint _lastRequestedAddonOverlayHeight;
    private XamlRoot? _previewXamlRoot;
    private double _previewZoom = 1.0;
    private double _previewPanX;
    private double _previewPanY;

    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        var windowHandle = WindowNative.GetWindowHandle(this);
        var paths = new AppPaths();
        ViewModel = new MainWindowViewModel(paths);
        _previewHost = new PreviewHostService(windowHandle, PreviewSurface);
        ViewModel.Initialize(new SettingsPickerService(() => windowHandle));
        NativeAddonOverlaySwitch.IsOn = true;
        ViewModel.ControlsChanged += BuildControls;
        ViewModel.AddonControlsChanged += BuildAddonControls;
        ViewModel.AddonControlValuesChanged += UpdateAddonControlValues;
        ViewModel.PreviewFrameReceived += frame => PreviewImage.Source = frame;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
                UpdateContentView();
            if (args.PropertyName == nameof(MainWindowViewModel.IsPreviewRunning))
            {
                _activeNativeAddonOverlayId = string.Empty;
                UpdatePreviewTransportView();
                UpdateAddonOverlayHostVisibility();
            }
            if (args.PropertyName == nameof(MainWindowViewModel.IsGalleryMode) ||
                args.PropertyName == nameof(MainWindowViewModel.IsGalleryPanelOpen))
            {
                UpdateGalleryView();
                UpdateInputModeSwitch();
            }
            if (args.PropertyName == nameof(MainWindowViewModel.SelectedGalleryItem))
                SyncGallerySelection();
            if (args.PropertyName == nameof(MainWindowViewModel.SharedPreviewHandle) ||
                args.PropertyName == nameof(MainWindowViewModel.SharedPreviewWidth) ||
                args.PropertyName == nameof(MainWindowViewModel.SharedPreviewHeight))
                EnsureGpuPreviewBridge();
            if (args.PropertyName == nameof(MainWindowViewModel.SharedAddonOverlayHandle) ||
                args.PropertyName == nameof(MainWindowViewModel.SharedAddonOverlayWidth) ||
                args.PropertyName == nameof(MainWindowViewModel.SharedAddonOverlayHeight))
            {
                if (args.PropertyName == nameof(MainWindowViewModel.SharedAddonOverlayHandle))
                {
                    _lastRequestedAddonOverlayWidth = 0;
                    _lastRequestedAddonOverlayHeight = 0;
                }
                EnsureAddonOverlayBridge();
            }
        };
        ViewModel.Settings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.PreviewTransport))
                UpdatePreviewTransportView();
            if (args.PropertyName == nameof(SettingsViewModel.DepthFormat))
                UpdateDepthFormatSelection();
            if (args.PropertyName == nameof(SettingsViewModel.DepthProfile))
            {
                UpdateDepthProfileSelection();
                UpdateDepthFormatAvailability();
                UpdateDepthDownsampleAvailability();
            }
            if (args.PropertyName == nameof(SettingsViewModel.DepthDownsample))
                UpdateDepthDownsampleSelection();
            if (args.PropertyName == nameof(SettingsViewModel.SliderDragSensitivity) ||
                args.PropertyName == nameof(SettingsViewModel.SliderSymLog))
                UpdateSliderInteractionMode();
        };
        PreviewSurface.PointerWheelChanged += OnPreviewPointerWheelChanged;
        PreviewSurface.PointerPressed += OnPreviewPointerPressed;
        PreviewSurface.PointerMoved += OnPreviewPointerMoved;
        PreviewSurface.PointerReleased += OnPreviewPointerReleased;
        PreviewSurface.PointerCanceled += OnPreviewPointerReleased;
        PreviewSurface.DoubleTapped += (_, _) => ResetPreviewView();
        PreviewSurface.Loaded += (_, _) => AttachPreviewXamlRootChanged();
        PreviewSurface.SizeChanged += (_, _) => UpdatePreviewClipAndTransform();
        ControlsScrollViewer.SizeChanged += (_, _) => UpdateControlsPanelWidth();
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        _gpuPreviewTimer.Interval = TimeSpan.FromMilliseconds(16);
        _gpuPreviewTimer.Tick += (_, _) => RenderGpuPreviewFrame();
        _addonOverlayPreviewTimer.Interval = TimeSpan.FromMilliseconds(16);
        _addonOverlayPreviewTimer.Tick += (_, _) => RenderAddonOverlayFrame();
        _addonInputMoveTimer.Interval = TimeSpan.FromMilliseconds(16);
        _addonInputMoveTimer.Tick += (_, _) => FlushAddonOverlayMouseMove();
        _sliderModeOverlayTimer.Interval = TimeSpan.FromMilliseconds(850);
        _sliderModeOverlayTimer.Tick += (_, _) =>
        {
            _sliderModeOverlayTimer.Stop();
            SliderModeOverlay.Visibility = Visibility.Collapsed;
        };
        Closed += (_, _) =>
        {
            if (_previewXamlRoot is not null)
                _previewXamlRoot.Changed -= OnPreviewXamlRootChanged;
            _previewHost.Dispose();
            _addonInputMoveTimer.Stop();
            _sliderModeOverlayTimer.Stop();
            _addonOverlayPreviewTimer.Stop();
            _d3dPreview.Dispose();
            _addonOverlayPreview.Dispose();
            ViewModel.AddonControlsChanged -= BuildAddonControls;
            ViewModel.AddonControlValuesChanged -= UpdateAddonControlValues;
            ViewModel.ControlsChanged -= BuildControls;
            ViewModel.Dispose();
        };

        BuildControls();
        BuildAddonControls();
        UpdateControlsPanelWidth();
        PreviewTransportBox.SelectedIndex = ViewModel.Settings.PreviewTransport == "CPU" ? 1 : 0;
        UpdateDepthFormatSelection();
        UpdateDepthProfileSelection();
        UpdateDepthFormatAvailability();
        UpdateDepthDownsampleSelection();
        UpdateDepthDownsampleAvailability();
        UpdateContentView();
        UpdateGalleryView();
        UpdateInputModeSwitch();
        UpdatePreviewTransportView();
        UpdateAddonOverlayHostVisibility();
    }

    private double PreviewRasterizationScale => PreviewSurface.XamlRoot?.RasterizationScale ?? 1.0;
    private double AddonOverlayRasterizationScale => AddonNativeSurface.XamlRoot?.RasterizationScale ?? 1.0;

    private void AttachPreviewXamlRootChanged()
    {
        var root = PreviewSurface.XamlRoot;
        if (root is null || ReferenceEquals(root, _previewXamlRoot))
            return;

        if (_previewXamlRoot is not null)
            _previewXamlRoot.Changed -= OnPreviewXamlRootChanged;

        _previewXamlRoot = root;
        _previewXamlRoot.Changed += OnPreviewXamlRootChanged;
    }

    private void OnPreviewXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdatePreviewClipAndTransform();
    }

    private void UpdateContentView()
    {
        var showSettings = ViewModel.IsSettingsOpen;
        MainContent.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsContent.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        UpdateGalleryView();
        UpdatePreviewTransportView();
        UpdateAddonOverlayHostVisibility();
    }

    private void OnControlsTabSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        UpdateAddonOverlayHostVisibility();
    }

    private void OnAddonOverlayTabSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_buildingAddonOverlayTabs)
            UpdateAddonOverlayHostVisibility();
    }

    private void OnNativeAddonOverlayToggled(object sender, RoutedEventArgs args)
    {
        UpdateAddonOverlayHostVisibility();
    }

    private void UpdateAddonOverlayHostVisibility()
    {
        var selectedOverlay = (AddonOverlayTabView.SelectedItem as TabViewItem)?.Tag as AddonOverlayViewModel;
        var visible = ViewModel.IsPreviewRunning &&
                      !ViewModel.IsSettingsOpen &&
                      ControlsTabView.SelectedIndex == 1 &&
                      NativeAddonOverlaySwitch.IsOn &&
                      selectedOverlay != null;

        AddonNativeSurfaceBorder.Visibility = selectedOverlay != null ? Visibility.Visible : Visibility.Collapsed;
        AddonOverlaySwapChainPanel.Visibility = visible && ViewModel.SharedAddonOverlayHandle != 0 ? Visibility.Visible : Visibility.Collapsed;
        AddonOverlayPlaceholder.Visibility = AddonOverlaySwapChainPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        AddonOverlayPlaceholder.Text = selectedOverlay == null
            ? "Select an add-on overlay tab"
            : !ViewModel.IsPreviewRunning
                ? "Start preview to load the native add-on overlay"
                : !NativeAddonOverlaySwitch.IsOn
                    ? "Native add-on overlays are turned off"
                    : "Preparing native add-on overlay";

        if (visible)
        {
            EnsureAddonOverlayBridge();
            _addonOverlayPreviewTimer.Start();
        }
        else
        {
            _addonOverlayPreviewTimer.Stop();
            if (!ViewModel.IsPreviewRunning)
            {
                _lastRequestedAddonOverlayWidth = 0;
                _lastRequestedAddonOverlayHeight = 0;
            }
        }

        _ = SyncNativeAddonOverlayAsync(visible ? selectedOverlay : null);
    }

    private async Task SyncNativeAddonOverlayAsync(AddonOverlayViewModel? overlay)
    {
        var requestedId = overlay?.Id ?? string.Empty;
        if (!ViewModel.IsPreviewRunning)
            return;
        if (string.Equals(requestedId, _activeNativeAddonOverlayId, StringComparison.Ordinal))
            return;

        if (await RunUiCommandAsync(() => ViewModel.SelectNativeAddonOverlayAsync(overlay)))
            _activeNativeAddonOverlayId = requestedId;
    }

    private void QueueAddonOverlayInput(PreviewHostInputMessage input)
    {
        const uint mouseMove = 0x0200;
        if (input.Message == mouseMove)
        {
            _pendingAddonMouseMove = input;
            if (!_addonInputMoveTimer.IsEnabled)
                _addonInputMoveTimer.Start();
            return;
        }

        _ = ViewModel.SendAddonOverlayInputAsync(input);
    }

    private void FlushAddonOverlayMouseMove()
    {
        if (_pendingAddonMouseMove is not { } input)
        {
            _addonInputMoveTimer.Stop();
            return;
        }

        _pendingAddonMouseMove = null;
        _ = ViewModel.SendAddonOverlayInputAsync(input);
    }

    private void OnAddonOverlayPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0200, 0, GetAddonOverlayPointerLParam(args)));
        args.Handled = true;
    }

    private void OnAddonOverlayPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        AddonOverlaySwapChainPanel.Focus(FocusState.Pointer);
        AddonOverlaySwapChainPanel.CapturePointer(args.Pointer);
        _capturedAddonPointerId = args.Pointer.PointerId;
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0200, 0, GetAddonOverlayPointerLParam(args)));

        var properties = args.GetCurrentPoint(AddonOverlaySwapChainPanel).Properties;
        if (properties.IsLeftButtonPressed)
            QueueAddonOverlayInput(new PreviewHostInputMessage(0x0201, 0, GetAddonOverlayPointerLParam(args)));
        else if (properties.IsRightButtonPressed)
            QueueAddonOverlayInput(new PreviewHostInputMessage(0x0204, 0, GetAddonOverlayPointerLParam(args)));
        else if (properties.IsMiddleButtonPressed)
            QueueAddonOverlayInput(new PreviewHostInputMessage(0x0207, 0, GetAddonOverlayPointerLParam(args)));

        args.Handled = true;
    }

    private void OnAddonOverlayPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        var kind = args.GetCurrentPoint(AddonOverlaySwapChainPanel).Properties.PointerUpdateKind;
        var message = kind switch
        {
            Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased => 0x0202u,
            Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => 0x0205u,
            Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => 0x0208u,
            _ => 0u
        };
        if (message != 0)
            QueueAddonOverlayInput(new PreviewHostInputMessage(message, 0, GetAddonOverlayPointerLParam(args)));
        _capturedAddonPointerId = null;
        AddonOverlaySwapChainPanel.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void OnAddonOverlayPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.IsInContact)
            return;

        ClearPendingAddonMouseMove();
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x02A3, 0, 0));
    }

    private void OnAddonOverlayPointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_capturedAddonPointerId != args.Pointer.PointerId)
            return;

        _capturedAddonPointerId = null;
        ClearPendingAddonMouseMove();
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0202, 0, 0));
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0205, 0, 0));
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0208, 0, 0));
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x02A3, 0, 0));
    }

    private void ClearPendingAddonMouseMove()
    {
        _pendingAddonMouseMove = null;
        _addonInputMoveTimer.Stop();
    }

    private void OnAddonOverlayPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var delta = args.GetCurrentPoint(AddonOverlaySwapChainPanel).Properties.MouseWheelDelta;
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x020A, (long)(delta << 16), GetAddonOverlayPointerLParam(args)));
        args.Handled = true;
    }

    private void OnAddonOverlayGotFocus(object sender, RoutedEventArgs args)
    {
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0007, 0, 0));
    }

    private void OnAddonOverlayLostFocus(object sender, RoutedEventArgs args)
    {
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0008, 0, 0));
    }

    private void OnAddonOverlayKeyDown(object sender, KeyRoutedEventArgs args)
    {
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0100, (long)args.Key, 0));
        args.Handled = true;
    }

    private void OnAddonOverlayKeyUp(object sender, KeyRoutedEventArgs args)
    {
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0101, (long)args.Key, 0));
        args.Handled = true;
    }

    private void OnAddonOverlayCharacterReceived(object sender, CharacterReceivedRoutedEventArgs args)
    {
        QueueAddonOverlayInput(new PreviewHostInputMessage(0x0102, args.Character, 0));
        args.Handled = true;
    }

    private void OnAddonOverlaySurfaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ResizeAddonOverlayBridge();
        _ = SyncAddonOverlaySurfaceSizeAsync();
    }

    private long GetAddonOverlayPointerLParam(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(AddonOverlaySwapChainPanel).Position;
        var scale = AddonOverlayRasterizationScale;
        var x = Math.Clamp((int)Math.Round(point.X * scale), 0, ushort.MaxValue);
        var y = Math.Clamp((int)Math.Round(point.Y * scale), 0, ushort.MaxValue);
        return (long)(uint)(x | (y << 16));
    }

    private void UpdatePreviewTransportView()
    {
        var useGpuPreview = ViewModel.Settings.PreviewTransport == "GPU" && ViewModel.IsPreviewRunning && !ViewModel.IsSettingsOpen;
        _previewHost.SetVisible(false);
        PreviewSwapChainPanel.Visibility = useGpuPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = useGpuPreview ? Visibility.Collapsed : Visibility.Visible;
        if (useGpuPreview)
        {
            EnsureGpuPreviewBridge();
            _gpuPreviewTimer.Start();
        }
        else
        {
            _gpuPreviewTimer.Stop();
        }
    }

    private void UpdateGalleryView()
    {
        var visible = ViewModel.IsGalleryMode && !ViewModel.IsSettingsOpen;
        GalleryPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        GalleryColumn.Width = visible
            ? new GridLength(ViewModel.IsGalleryPanelOpen ? 280.0 : 44.0)
            : new GridLength(0.0);
        GalleryExpandedContent.Visibility = visible && ViewModel.IsGalleryPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        GalleryTitle.Visibility = ViewModel.IsGalleryPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        SyncGallerySelection();
    }

    private void UpdateInputModeSwitch()
    {
        _updatingInputModeSwitch = true;
        InputModeSwitch.IsOn = ViewModel.IsGalleryMode;
        _updatingInputModeSwitch = false;
    }

    private async void OnInputModeSwitchToggled(object sender, RoutedEventArgs args)
    {
        if (_updatingInputModeSwitch)
            return;

        if (InputModeSwitch.IsOn == ViewModel.IsGalleryMode)
            return;

        await ViewModel.ToggleInputModeAsync();
        UpdateInputModeSwitch();
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

        if (!item.IsValid)
        {
            SyncGallerySelection();
            return;
        }

        await RunUiCommandAsync(() => ViewModel.SelectGalleryItemAsync(item));
    }

    private void OnPreviewTransportSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ViewModel == null)
            return;

        if (PreviewTransportBox.SelectedItem is ComboBoxItem item && item.Tag is string transport)
        {
            ViewModel.Settings.PreviewTransport = transport;
            UpdatePreviewTransportView();
        }
    }

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

    private void OnSliderDragSensitivityWheelChanged(object? sender, MappedSliderWheelEventArgs args)
    {
        var factors = new[] { 1, 2, 4, 8 };
        var currentIndex = Array.IndexOf(factors, ViewModel.Settings.SliderDragSensitivity);
        if (currentIndex < 0)
            currentIndex = 0;
        var nextIndex = Math.Clamp(currentIndex + (args.Delta > 0 ? 1 : -1), 0, factors.Length - 1);
        ViewModel.Settings.SliderDragSensitivity = factors[nextIndex];
        ShowSliderModeOverlay("x" + factors[nextIndex].ToString(CultureInfo.InvariantCulture));
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.L || args.KeyStatus.WasKeyDown || IsTextEntryFocused())
            return;
        if (_fxMappedSliders.Any(static slider => slider.IsDragging) ||
            _addonMappedSliders.Any(static slider => slider.IsDragging))
            return;

        ViewModel.Settings.SliderSymLog = !ViewModel.Settings.SliderSymLog;
        ShowSliderModeOverlay(ViewModel.Settings.SliderSymLog ? "L  SYMLOG" : "LINEAR");
        args.Handled = true;
    }

    private void UpdateSliderInteractionMode()
    {
        var sensitivity = ViewModel.Settings.SliderDragSensitivity;
        var logarithmic = ViewModel.Settings.SliderSymLog;
        foreach (var slider in _fxMappedSliders.Concat(_addonMappedSliders))
        {
            if (!slider.IsDragging)
                slider.SetInteractionMode(sensitivity, logarithmic);
        }

        var visibility = logarithmic ? Visibility.Visible : Visibility.Collapsed;
        foreach (var indicator in _fxLogIndicators.Concat(_addonLogIndicators))
            indicator.Visibility = visibility;
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

    private void OnPreviewPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(PreviewSurface);
        var delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        var position = point.Position;
        var oldZoom = _previewZoom;
        var zoomFactor = delta > 0 ? 1.15 : 1.0 / 1.15;
        var newZoom = Math.Clamp(oldZoom * zoomFactor, 1.0, 16.0);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
            return;

        _previewPanX = position.X - (position.X - _previewPanX) * (newZoom / oldZoom);
        _previewPanY = position.Y - (position.Y - _previewPanY) * (newZoom / oldZoom);
        _previewZoom = newZoom;
        ClampAndApplyPreviewTransform();
        args.Handled = true;
    }

    private void OnPreviewPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(PreviewSurface);
        if (point.Properties.IsMiddleButtonPressed)
        {
            ResetPreviewView();
            args.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        _isPreviewDragging = true;
        _lastPreviewDragPoint = point.Position;
        PreviewSurface.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void OnPreviewPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_isPreviewDragging)
            return;

        var point = args.GetCurrentPoint(PreviewSurface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndPreviewDrag(args);
            return;
        }

        var position = point.Position;
        _previewPanX += position.X - _lastPreviewDragPoint.X;
        _previewPanY += position.Y - _lastPreviewDragPoint.Y;
        _lastPreviewDragPoint = position;
        ClampAndApplyPreviewTransform();
        args.Handled = true;
    }

    private void OnPreviewPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        EndPreviewDrag(args);
    }

    private void EndPreviewDrag(PointerRoutedEventArgs args)
    {
        if (!_isPreviewDragging)
            return;

        _isPreviewDragging = false;
        PreviewSurface.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    private void ResetPreviewView()
    {
        _previewZoom = 1.0;
        _previewPanX = 0.0;
        _previewPanY = 0.0;
        ClampAndApplyPreviewTransform();
    }

    private void ClampAndApplyPreviewTransform()
    {
        if (_previewZoom <= 1.0001)
        {
            _previewZoom = 1.0;
            _previewPanX = 0.0;
            _previewPanY = 0.0;
        }
        else
        {
            var width = Math.Max(1.0, PreviewSurface.ActualWidth);
            var height = Math.Max(1.0, PreviewSurface.ActualHeight);
            var minX = width - width * _previewZoom;
            var minY = height - height * _previewZoom;
            _previewPanX = Math.Clamp(_previewPanX, minX, 0.0);
            _previewPanY = Math.Clamp(_previewPanY, minY, 0.0);
        }

        PreviewTransform.ScaleX = _previewZoom;
        PreviewTransform.ScaleY = _previewZoom;
        PreviewTransform.TranslateX = _previewPanX;
        PreviewTransform.TranslateY = _previewPanY;
        if (ViewModel.Settings.PreviewTransport == "GPU")
        {
            PreviewTransform.ScaleX = 1.0;
            PreviewTransform.ScaleY = 1.0;
            PreviewTransform.TranslateX = 0.0;
            PreviewTransform.TranslateY = 0.0;
            UpdateGpuPreviewViewTransform();
        }
    }

    private void UpdatePreviewClipAndTransform()
    {
        PreviewSurface.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, Math.Max(0, PreviewSurface.ActualWidth), Math.Max(0, PreviewSurface.ActualHeight))
        };
        ResizeGpuPreviewBridge();
        ClampAndApplyPreviewTransform();
    }

    private void EnsureGpuPreviewBridge()
    {
        if (ViewModel.Settings.PreviewTransport != "GPU" ||
            !ViewModel.IsPreviewRunning ||
            ViewModel.SharedPreviewHandle == 0 ||
            ViewModel.SharedPreviewWidth == 0 ||
            ViewModel.SharedPreviewHeight == 0)
            return;

        var rasterizationScale = PreviewRasterizationScale;
        var width = Math.Max(1u, (uint)Math.Round(PreviewSurface.ActualWidth * rasterizationScale));
        var height = Math.Max(1u, (uint)Math.Round(PreviewSurface.ActualHeight * rasterizationScale));
        _d3dPreview.EnsureCreated(PreviewSwapChainPanel, width, height);
        UpdateGpuPreviewViewTransform();
        _d3dPreview.Render(ViewModel.SharedPreviewHandle, ViewModel.SharedPreviewWidth, ViewModel.SharedPreviewHeight);
    }

    private void ResizeGpuPreviewBridge()
    {
        if (!_d3dPreview.IsCreated)
            return;

        var rasterizationScale = PreviewRasterizationScale;
        var width = Math.Max(1u, (uint)Math.Round(PreviewSurface.ActualWidth * rasterizationScale));
        var height = Math.Max(1u, (uint)Math.Round(PreviewSurface.ActualHeight * rasterizationScale));
        _d3dPreview.Resize(width, height);
        UpdateGpuPreviewViewTransform();
    }

    private void UpdateGpuPreviewViewTransform()
    {
        if (ViewModel.SharedPreviewWidth == 0 || ViewModel.SharedPreviewHeight == 0)
        {
            _d3dPreview.SetViewTransform(0.0, 0.0, 1.0, 1.0);
            return;
        }

        var rasterizationScale = PreviewRasterizationScale;
        var panelWidth = Math.Max(1.0, PreviewSurface.ActualWidth * rasterizationScale);
        var panelHeight = Math.Max(1.0, PreviewSurface.ActualHeight * rasterizationScale);
        var panX = _previewPanX * rasterizationScale;
        var panY = _previewPanY * rasterizationScale;
        var sourceAspect = (double)ViewModel.SharedPreviewWidth / ViewModel.SharedPreviewHeight;
        var panelAspect = panelWidth / panelHeight;

        double fitWidth;
        double fitHeight;
        double fitX;
        double fitY;
        if (panelAspect > sourceAspect)
        {
            fitHeight = panelHeight;
            fitWidth = fitHeight * sourceAspect;
            fitX = (panelWidth - fitWidth) * 0.5;
            fitY = 0.0;
        }
        else
        {
            fitWidth = panelWidth;
            fitHeight = fitWidth / sourceAspect;
            fitX = 0.0;
            fitY = (panelHeight - fitHeight) * 0.5;
        }

        var zoom = Math.Max(1.0, _previewZoom);
        var destX = fitX * zoom + panX;
        var destY = fitY * zoom + panY;
        var destWidth = fitWidth * zoom;
        var destHeight = fitHeight * zoom;
        _d3dPreview.SetViewTransform(destX, destY, destWidth, destHeight);
    }

    private void RenderGpuPreviewFrame()
    {
        if (ViewModel.Settings.PreviewTransport != "GPU" || !ViewModel.IsPreviewRunning || ViewModel.IsSettingsOpen)
            return;

        EnsureGpuPreviewBridge();
        _d3dPreview.RenderLastFrame();
    }

    private void EnsureAddonOverlayBridge()
    {
        if (!ViewModel.IsPreviewRunning ||
            ViewModel.IsSettingsOpen ||
            ControlsTabView.SelectedIndex != 1 ||
            !NativeAddonOverlaySwitch.IsOn ||
            ViewModel.SharedAddonOverlayHandle == 0 ||
            ViewModel.SharedAddonOverlayWidth == 0 ||
            ViewModel.SharedAddonOverlayHeight == 0)
            return;

        var width = Math.Max(1u, (uint)Math.Round(AddonNativeSurface.ActualWidth * AddonOverlayRasterizationScale));
        var height = Math.Max(1u, (uint)Math.Round(AddonNativeSurface.ActualHeight * AddonOverlayRasterizationScale));
        _addonOverlayPreview.EnsureCreated(AddonOverlaySwapChainPanel, width, height);
        _addonOverlayPreview.SetViewTransform(0.0, 0.0, width, height);
        _addonOverlayPreview.Render(ViewModel.SharedAddonOverlayHandle, ViewModel.SharedAddonOverlayWidth, ViewModel.SharedAddonOverlayHeight);

        AddonOverlaySwapChainPanel.Visibility = Visibility.Visible;
        AddonOverlayPlaceholder.Visibility = Visibility.Collapsed;
        _ = SyncAddonOverlaySurfaceSizeAsync();
    }

    private void ResizeAddonOverlayBridge()
    {
        if (!_addonOverlayPreview.IsCreated)
            return;

        var width = Math.Max(1u, (uint)Math.Round(AddonNativeSurface.ActualWidth * AddonOverlayRasterizationScale));
        var height = Math.Max(1u, (uint)Math.Round(AddonNativeSurface.ActualHeight * AddonOverlayRasterizationScale));
        _addonOverlayPreview.Resize(width, height);
        _addonOverlayPreview.SetViewTransform(0.0, 0.0, width, height);
    }

    private async Task SyncAddonOverlaySurfaceSizeAsync()
    {
        if (!ViewModel.IsPreviewRunning ||
            ViewModel.IsSettingsOpen ||
            ControlsTabView.SelectedIndex != 1 ||
            !NativeAddonOverlaySwitch.IsOn)
            return;

        var width = Math.Max(1u, (uint)Math.Round(AddonNativeSurface.ActualWidth * AddonOverlayRasterizationScale));
        var height = Math.Max(1u, (uint)Math.Round(AddonNativeSurface.ActualHeight * AddonOverlayRasterizationScale));
        if (width == _lastRequestedAddonOverlayWidth && height == _lastRequestedAddonOverlayHeight)
            return;

        _lastRequestedAddonOverlayWidth = width;
        _lastRequestedAddonOverlayHeight = height;
        try
        {
            await ViewModel.SetAddonOverlaySurfaceSizeAsync(width, height);
        }
        catch
        {
            _lastRequestedAddonOverlayWidth = 0;
            _lastRequestedAddonOverlayHeight = 0;
        }
    }

    private void RenderAddonOverlayFrame()
    {
        EnsureAddonOverlayBridge();
    }

    private void BuildControls()
    {
        ControlsPanel.Children.Clear();
        _fxMappedSliders.Clear();
        _fxLogIndicators.Clear();
        AddonsPanel.Children.Clear();
        AddonControlsPanel.Children.Clear();
        UpdateControlsPanelWidth();

        var effectsEnabled = new CheckBox
        {
            Content = "Effects Enabled",
            IsChecked = ViewModel.EffectsEnabled,
            Margin = new Thickness(0, 0, 0, 8)
        };
        effectsEnabled.Checked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetEffectsEnabledAsync(true));
        effectsEnabled.Unchecked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetEffectsEnabledAsync(false));
        ControlsPanel.Children.Add(effectsEnabled);

        var techniquePanel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var technique in ViewModel.Techniques)
        {
            var checkBox = new CheckBox
            {
                Content = technique.Label,
                IsChecked = technique.IsEnabled,
                Tag = technique
            };
            checkBox.Checked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetTechniqueStateAsync((TechniqueViewModel)checkBox.Tag, true));
            checkBox.Unchecked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetTechniqueStateAsync((TechniqueViewModel)checkBox.Tag, false));
            techniquePanel.Children.Add(checkBox);
        }
        ControlsPanel.Children.Add(CreateSectionExpander("Techniques", true, techniquePanel));

        foreach (var effect in ViewModel.Effects)
        {
            var expander = CreateSectionExpander(CreateEffectHeader(effect), false, BuildEffectPanel(effect), new Thickness(0, 0, 0, 0));
            expander.Tag = effect.Name;
            expander.AllowDrop = true;
            expander.DragOver += OnEffectDragOver;
            expander.Drop += OnEffectDrop;
            expander.Margin = new Thickness(0, 0, 0, 6);
            ControlsPanel.Children.Add(expander);
        }

        if (ViewModel.Techniques.Count == 0 && ViewModel.Effects.Count == 0)
        {
            ControlsPanel.Children.Add(new TextBlock
            {
                Text = "Start preview to load ReShade controls.",
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap
            });
        }

        BuildAddons();
        BuildAddonControls();
    }

    private void BuildAddons()
    {
        AddonsPanel.Children.Clear();
        BuildNativeAddonOverlayTabs();

        var pathsPanel = new StackPanel { Spacing = 4 };
        pathsPanel.Children.Add(new TextBlock
        {
            Text = "AddonPath",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        pathsPanel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(ViewModel.AddonSearchPath) ? "Not reported by the ReShade runtime" : ViewModel.AddonSearchPath,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        pathsPanel.Children.Add(new TextBlock
        {
            Text = "ReShade log: " + (string.IsNullOrWhiteSpace(ViewModel.AddonLogPath) ? "Not available" : ViewModel.AddonLogPath),
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        AddonsPanel.Children.Add(pathsPanel);

        var failedCount = ViewModel.AddonDiagnostics.Count(static diagnostic => diagnostic.IsFailure);
        var disabledCount = ViewModel.AddonDiagnostics.Count(static diagnostic => diagnostic.IsDisabled);
        var loadedCount = ViewModel.AddonDiagnostics.Count(static diagnostic => diagnostic.IsLoaded);
        AddonsPanel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = failedCount != 0 || !ViewModel.AllAddonsLoaded
                ? InfoBarSeverity.Error
                : disabledCount != 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
            Title = failedCount != 0 || !ViewModel.AllAddonsLoaded ? "Some add-ons did not load" : "Add-on scan completed",
            Message = $"{loadedCount} loaded, {disabledCount} disabled, {failedCount} failed or skipped. Add-on changes require restarting the preview runtime."
        });

        if (ViewModel.AddonDiagnostics.Count != 0)
        {
            var diagnosticsPanel = new StackPanel { Spacing = 8 };
            foreach (var diagnostic in ViewModel.AddonDiagnostics)
            {
                var diagnosticPanel = new StackPanel { Spacing = 3 };
                diagnosticPanel.Children.Add(new TextBlock
                {
                    Text = diagnostic.DisplayName,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });
                diagnosticPanel.Children.Add(new TextBlock
                {
                    Text = diagnostic.StatusText,
                    TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrWhiteSpace(diagnostic.DetailText))
                {
                    diagnosticPanel.Children.Add(new TextBlock
                    {
                        Text = diagnostic.DetailText,
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                if (!string.IsNullOrWhiteSpace(diagnostic.Path))
                {
                    diagnosticPanel.Children.Add(new TextBlock
                    {
                        Text = diagnostic.Path,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    });
                }

                diagnosticsPanel.Children.Add(new Border
                {
                    Padding = new Thickness(10),
                    BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    Child = diagnosticPanel
                });
            }
            AddonsPanel.Children.Add(CreateSectionExpander("Load diagnostics", failedCount != 0, diagnosticsPanel));
        }

        if (ViewModel.AddonLogErrors.Count != 0)
        {
            var logPanel = new StackPanel { Spacing = 6 };
            foreach (var line in ViewModel.AddonLogErrors)
            {
                logPanel.Children.Add(new TextBlock
                {
                    Text = line,
                    FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                });
            }
            AddonsPanel.Children.Add(CreateSectionExpander("Add-on initialization log", true, logPanel));
        }

        var nativePanelButton = new Button
        {
            Content = "Open native add-on panel",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(nativePanelButton, "BtnOpenNativeAddonPanel");
        nativePanelButton.Click += async (_, _) => await RunUiCommandAsync(ViewModel.OpenNativeAddonPanelAsync);
        AddonsPanel.Children.Add(nativePanelButton);
        AddonsPanel.Children.Add(new TextBlock
        {
            Text = "Use this fallback for complex add-on UI that cannot be represented by standard WinUI controls yet.",
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap
        });

        if (ViewModel.Addons.Count == 0)
        {
            AddonsPanel.Children.Add(new TextBlock
            {
                Text = "No ReShade add-on registered successfully. Review the load diagnostics and ReShade log above.",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var addon in ViewModel.Addons)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = addon.StatusText + " - " + addon.OverlayText,
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = addon.OfflineCompatibility == "runtime_compatible" ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Title = addon.CompatibilityText,
                Message = addon.Events.Count == 0
                    ? "The add-on did not register event callbacks. UI-only add-ons may still work normally."
                    : $"{addon.Events.Count(static item => item.IsSupported)} of {addon.Events.Count} registered event types are available in the offline host."
            });

            if (!string.IsNullOrWhiteSpace(addon.SourceText))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = addon.SourceText,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                });
            }

            if (!string.IsNullOrWhiteSpace(addon.Description))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = addon.Description,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            if (addon.HasSettingsOverlay)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Settings overlay registered",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }

            if (addon.HasEventOverlay)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Event overlay registered",
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
            }

            foreach (var overlay in addon.Overlays)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Overlay: " + overlay,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            if (addon.Events.Count != 0)
            {
                var eventsPanel = new StackPanel { Spacing = 4 };
                foreach (var addonEvent in addon.Events)
                {
                    var eventRow = new Grid { ColumnSpacing = 8 };
                    eventRow.ColumnDefinitions.Add(new ColumnDefinition());
                    eventRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    eventRow.Children.Add(new TextBlock
                    {
                        Text = addonEvent.Name,
                        FontFamily = new FontFamily("Consolas"),
                        TextWrapping = TextWrapping.Wrap
                    });
                    var support = new TextBlock
                    {
                        Text = addonEvent.SupportText,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 210
                    };
                    Grid.SetColumn(support, 1);
                    eventRow.Children.Add(support);
                    eventsPanel.Children.Add(eventRow);
                }
                panel.Children.Add(CreateSectionExpander("Registered events", false, eventsPanel));
            }

            AddonsPanel.Children.Add(CreateSectionExpander(addon.Name, false, panel));
        }
    }

    private void BuildNativeAddonOverlayTabs()
    {
        var previousSelection = (AddonOverlayTabView.SelectedItem as TabViewItem)?.Tag as AddonOverlayViewModel;
        _buildingAddonOverlayTabs = true;
        try
        {
            AddonOverlayTabView.TabItems.Clear();
            var selectedIndex = -1;
            foreach (var overlay in ViewModel.AddonOverlays)
            {
                var tab = new TabViewItem
                {
                    Header = overlay.DisplayTitle,
                    IsClosable = false,
                    Tag = overlay
                };
                AutomationProperties.SetAutomationId(tab, "AddonOverlay_" + MakeAutomationId(overlay.Id));
                AutomationProperties.SetName(tab, overlay.DisplayTitle);
                AddonOverlayTabView.TabItems.Add(tab);
                if (previousSelection != null && string.Equals(previousSelection.Id, overlay.Id, StringComparison.Ordinal))
                    selectedIndex = AddonOverlayTabView.TabItems.Count - 1;
            }

            AddonOverlayTabView.Visibility = AddonOverlayTabView.TabItems.Count != 0 ? Visibility.Visible : Visibility.Collapsed;
            NativeAddonOverlaySwitch.IsEnabled = AddonOverlayTabView.TabItems.Count != 0;
            AddonOverlayTabView.SelectedIndex = selectedIndex >= 0 ? selectedIndex : AddonOverlayTabView.TabItems.Count != 0 ? 0 : -1;
        }
        finally
        {
            _buildingAddonOverlayTabs = false;
        }

        UpdateAddonOverlayHostVisibility();
    }

    private void BuildAddonControls()
    {
        AddonControlsPanel.Children.Clear();
        _addonControlValueUpdaters.Clear();
        _addonMappedSliders.Clear();
        _addonLogIndicators.Clear();

        if (ViewModel.AddonImGuiControls.Count == 0)
        {
            AddonControlsPanel.Children.Add(new TextBlock
            {
                Text = "No standard add-on ImGui controls captured yet. Complex custom draw-list UI still needs the native panel fallback.",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var group in ViewModel.AddonImGuiControls.GroupBy(static control => control.GroupKey))
        {
            var controls = group.ToArray();
            var index = 0;
            var panel = BuildAddonControlSequence(controls, ref index);

            AddonControlsPanel.Children.Add(CreateSectionExpander(group.Key, true, panel, new Thickness(0, 0, 0, 0)));
        }
    }

    private StackPanel BuildAddonControlSequence(IReadOnlyList<AddonImGuiControlViewModel> controls, ref int index, params string[] stopKinds)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        FrameworkElement? lastElement = null;

        while (index < controls.Count)
        {
            var control = controls[index];
            if (stopKinds.Contains(control.Kind, StringComparer.Ordinal))
                break;

            if (control.IsTreeNode || control.IsCollapsingHeader)
            {
                ++index;
                StackPanel childPanel;
                if (control.IsTreeNode && control.IsOpen)
                {
                    childPanel = BuildAddonControlSequence(controls, ref index, "tree_end");
                    if (index < controls.Count && controls[index].IsTreeEnd)
                        ++index;
                }
                else if (control.IsCollapsingHeader && control.IsOpen)
                {
                    // ImGui collapsing headers have no matching End call. Treat the next
                    // header as a sibling boundary, which matches the common add-on layout.
                    childPanel = BuildAddonControlSequence(controls, ref index, "collapsing_header");
                }
                else
                {
                    childPanel = new StackPanel { Spacing = 8 };
                }

                var expander = CreateAddonNestedSectionExpander(control, childPanel, "AddonTree_");
                panel.Children.Add(expander);
                lastElement = expander;
                continue;
            }

            if (control.IsCollapsingHeader)
            {
                ++index;
                var childPanel = control.IsOpen
                    ? BuildAddonControlSequence(controls, ref index, stopKinds.Append("collapsing_header").Distinct(StringComparer.Ordinal).ToArray())
                    : new StackPanel { Spacing = 8 };
                var expander = CreateAddonNestedSectionExpander(control, childPanel, "AddonHeader_");
                panel.Children.Add(expander);
                lastElement = expander;
                continue;
            }

            if (control.IsTabBarBegin)
            {
                ++index;
                if (!control.IsOpen)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = control.Label,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap
                    });
                    continue;
                }
                var tabView = BuildAddonTabBar(controls, ref index);
                panel.Children.Add(tabView);
                lastElement = tabView;
                continue;
            }

            if (control.IsPopupBegin)
            {
                ++index;
                var popupPanel = control.IsOpen
                    ? BuildAddonControlSequence(controls, ref index, "popup_end")
                    : new StackPanel { Spacing = 8 };
                if (index < controls.Count && controls[index].IsPopupEnd)
                    ++index;
                var popupExpander = CreateAddonNestedSectionExpander(control, popupPanel, "AddonPopup_");
                panel.Children.Add(popupExpander);
                lastElement = popupExpander;
                continue;
            }

            if (control.IsMenuBegin)
            {
                ++index;
                var menuPanel = control.IsOpen
                    ? BuildAddonControlSequence(controls, ref index, "menu_end")
                    : new StackPanel { Spacing = 8 };
                if (index < controls.Count && controls[index].IsMenuEnd)
                    ++index;
                var menuExpander = CreateAddonNestedSectionExpander(control, menuPanel, "AddonMenu_");
                panel.Children.Add(menuExpander);
                lastElement = menuExpander;
                continue;
            }

            if (control.IsTooltip)
            {
                ++index;
                if (lastElement != null)
                    ToolTipService.SetToolTip(lastElement, control.Label);
                continue;
            }

            if (control.IsTreeEnd || control.IsTabBarEnd || control.IsTabItemEnd || control.IsPopupEnd || control.IsMenuEnd)
                break;

            var editor = BuildAddonControlEditor(control);
            panel.Children.Add(editor);
            lastElement = editor;
            ++index;
        }

        return panel;
    }

    private FrameworkElement BuildAddonTabBar(IReadOnlyList<AddonImGuiControlViewModel> controls, ref int index)
    {
        var tabView = new TabView
        {
            IsAddTabButtonVisible = false,
            CanDragTabs = false,
            TabWidthMode = TabViewWidthMode.SizeToContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 96
        };
        AutomationProperties.SetAutomationId(tabView, "AddonControlTabs_" + index.ToString(CultureInfo.InvariantCulture));
        TabViewItem? activeTab = null;

        while (index < controls.Count && !controls[index].IsTabBarEnd)
        {
            if (!controls[index].IsTabItemBegin)
            {
                ++index;
                continue;
            }

            var tabControl = controls[index++];
            var itemControls = new List<AddonImGuiControlViewModel>();
            while (index < controls.Count &&
                   !controls[index].IsTabItemBegin &&
                   !controls[index].IsTabBarEnd)
            {
                if (controls[index].IsTabItemEnd)
                {
                    ++index;
                    break;
                }
                itemControls.Add(controls[index++]);
            }

            var itemIndex = 0;
            var tab = new TabViewItem
            {
                Header = tabControl.Label,
                IsClosable = false,
                Tag = tabControl,
                Content = BuildAddonControlSequence(itemControls, ref itemIndex)
            };
            ApplyAddonAutomation(tab, tabControl);
            AutomationProperties.SetAutomationId(tab, "AddonControlTab_" + MakeAutomationId(tabControl.Id));
            tabView.TabItems.Add(tab);
            if (tabControl.IsOpen)
                activeTab = tab;
        }

        if (index < controls.Count && controls[index].IsTabBarEnd)
            ++index;
        tabView.SelectedItem = activeTab ?? tabView.TabItems.FirstOrDefault();
        tabView.SelectionChanged += async (_, _) =>
        {
            if (tabView.SelectedItem is TabViewItem { Tag: AddonImGuiControlViewModel selected })
                await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync(selected, "true"));
        };
        return tabView;
    }

    private FrameworkElement BuildAddonControlEditor(AddonImGuiControlViewModel control)
    {
        if (control.IsText)
        {
            return new TextBlock
            {
                Text = control.Label,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
        }

        if (control.IsTextInput)
            return BuildAddonTextEditor(control);

        if (control.IsColor)
            return BuildAddonColorEditor(control);

        if (control.IsNativeFallback)
        {
            return new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = control.Label,
                Message = string.IsNullOrWhiteSpace(control.Value) ? "This element requires the native add-on overlay." : control.Value
            };
        }

        if (control.IsMenuItem)
        {
            var button = new Button
            {
                Content = control.BoolValue ? "[x] " + control.Label : control.Label,
                Tag = control,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplyAddonAutomation(button, control);
            AutomationProperties.SetAutomationId(button, "AddonMenuItem_" + MakeAutomationId(control.Id));
            button.Click += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync(control, control.BoolValue ? "false" : "true"));
            return button;
        }

        if (control.IsButton)
        {
            var button = new Button
            {
                Content = control.Label,
                Tag = control,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplyAddonAutomation(button, control);
            AutomationProperties.SetAutomationId(button, "AddonButton_" + MakeAutomationId(control.Id));
            button.Click += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync((AddonImGuiControlViewModel)button.Tag, "click"));
            return button;
        }

        if (control.IsCheckbox)
        {
            var checkBox = new CheckBox
            {
                Content = control.Label,
                IsChecked = control.BoolValue,
                Tag = control
            };
            ApplyAddonAutomation(checkBox, control);
            AutomationProperties.SetAutomationId(checkBox, "AddonCheckbox_" + MakeAutomationId(control.Id));
            checkBox.Checked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync((AddonImGuiControlViewModel)checkBox.Tag, "true"));
            checkBox.Unchecked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync((AddonImGuiControlViewModel)checkBox.Tag, "false"));
            return checkBox;
        }

        if (control.IsCombo && control.Items.Count != 0)
        {
            return BuildAddonComboEditor(control);
        }

        if (control.IsNumeric || control.IsCombo)
        {
            return BuildAddonNumericEditor(control);
        }

        return new TextBlock
        {
            Text = control.Label,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private FrameworkElement BuildAddonTextEditor(AddonImGuiControlViewModel control)
    {
        var textBox = new TextBox
        {
            Header = control.Label,
            Text = control.Value,
            PlaceholderText = control.Minimum,
            AcceptsReturn = control.IsMultilineTextInput,
            TextWrapping = control.IsMultilineTextInput ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = control.IsMultilineTextInput ? 96 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = control
        };
        ApplyAddonAutomation(textBox, control);
        AutomationProperties.SetAutomationId(textBox, "AddonText_" + MakeAutomationId(control.Id));
        textBox.TextChanged += (_, _) =>
        {
            if (!_updatingAddonControlValues)
                QueueAddonControlUpdate(control, textBox.Text);
        };
        RegisterAddonControlValueUpdater(control.Id, updated =>
        {
            if (textBox.FocusState == FocusState.Unfocused)
                textBox.Text = updated.Value;
        });
        return textBox;
    }

    private FrameworkElement BuildAddonColorEditor(AddonImGuiControlViewModel control)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock
        {
            Text = control.Label,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        var values = control.NumericValues;
        byte ToByte(int component, double fallback) => (byte)Math.Clamp(Math.Round((component < values.Count ? values[component] : fallback) * 255.0), 0.0, 255.0);
        var picker = new ColorPicker
        {
            Color = Microsoft.UI.ColorHelper.FromArgb(ToByte(3, 1.0), ToByte(0, 0.0), ToByte(1, 0.0), ToByte(2, 0.0)),
            IsAlphaEnabled = control.Components >= 4,
            IsAlphaSliderVisible = control.Components >= 4,
            IsAlphaTextInputVisible = control.Components >= 4,
            IsMoreButtonVisible = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = control
        };
        ApplyAddonAutomation(picker, control);
        AutomationProperties.SetAutomationId(picker, "AddonColor_" + MakeAutomationId(control.Id));
        picker.ColorChanged += (_, args) =>
        {
            var color = args.NewColor;
            var value = string.Create(CultureInfo.InvariantCulture, $"{color.R / 255.0:0.######}, {color.G / 255.0:0.######}, {color.B / 255.0:0.######}");
            if (control.Components >= 4)
                value += string.Create(CultureInfo.InvariantCulture, $", {color.A / 255.0:0.######}");
            QueueAddonControlUpdate(control, value);
        };
        panel.Children.Add(picker);
        return panel;
    }

    private FrameworkElement BuildAddonComboEditor(AddonImGuiControlViewModel control)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = control.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        var combo = new ComboBox
        {
            Tag = control,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ApplyAddonAutomation(combo, control);
        AutomationProperties.SetAutomationId(combo, "AddonCombo_" + MakeAutomationId(control.Id));
        foreach (var item in control.Items)
            combo.Items.Add(item);
        combo.SelectedIndex = Math.Clamp((int)Math.Round(control.NumericValue), 0, Math.Max(0, combo.Items.Count - 1));
        combo.SelectionChanged += async (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync((AddonImGuiControlViewModel)combo.Tag, combo.SelectedIndex.ToString(CultureInfo.InvariantCulture)));
        };
        panel.Children.Add(combo);
        return panel;
    }

    private FrameworkElement BuildAddonNumericEditor(AddonImGuiControlViewModel control)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(control.IsCombo
            ? new TextBlock { Text = control.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }
            : CreateMappedSliderLabel(control.Label, _addonLogIndicators));

        var componentCount = Math.Max(1, control.Components);
        var numericValues = control.NumericValues.ToList();
        while (numericValues.Count < componentCount)
            numericValues.Add(numericValues.Count == 0 ? 0.0 : numericValues[^1]);

        var hasMinimum = TryParseFirstNumber(control.Minimum, out var minimum);
        var hasMaximum = TryParseFirstNumber(control.Maximum, out var maximum);
        var hasExplicitRange = hasMinimum && hasMaximum && maximum > minimum;
        var isInteger = control.Kind.Contains("int", StringComparison.OrdinalIgnoreCase);
        var editors = new List<(MappedSlider? Slider, TextBox TextBox)>();

        string FormatValue(double value) => isInteger
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture);

        string BuildValue()
        {
            return string.Join(", ", editors.Select(editor =>
                TryParseFirstNumber(editor.TextBox.Text, out var value) ? FormatValue(value) : FormatValue(0.0)));
        }

        async Task CommitValueAsync()
        {
            foreach (var editor in editors)
            {
                if (editor.Slider is null || !TryParseFirstNumber(editor.TextBox.Text, out var value))
                    continue;

                editor.Slider.SetMappedValue(value, false);
                editor.TextBox.Text = FormatValue(editor.Slider.MappedValue);
            }
            await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync(control, BuildValue()));
        }

        for (var component = 0; component < componentCount; ++component)
        {
            var componentIndex = component;
            var componentValue = numericValues[component];
            var row = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

            var textBox = new TextBox
            {
                Text = FormatValue(componentValue),
                Tag = control,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ApplyAddonAutomation(textBox, control);
            AutomationProperties.SetAutomationId(textBox, "AddonNumber_" + MakeAutomationId(control.Id) + "_" + componentIndex.ToString(CultureInfo.InvariantCulture));

            MappedSlider? slider = null;
            if (!control.IsCombo)
            {
                var dynamicSpan = Math.Max(isInteger ? 1.0 : 1.0, Math.Abs(componentValue) * 2.0);
                var componentMinimum = hasExplicitRange ? minimum : componentValue - dynamicSpan;
                var componentMaximum = hasExplicitRange ? maximum : componentValue + dynamicSpan;
                slider = new MappedSlider
                {
                    Tag = control,
                    MinWidth = 80,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                slider.ConfigureRange(componentMinimum, componentMaximum, componentValue, isInteger ? 1.0 : 0.001);
                slider.SetInteractionMode(ViewModel.Settings.SliderDragSensitivity, ViewModel.Settings.SliderSymLog);
                slider.DragSensitivityWheelChanged += OnSliderDragSensitivityWheelChanged;
                _addonMappedSliders.Add(slider);
                AutomationProperties.SetAutomationId(slider, "AddonSlider_" + MakeAutomationId(control.Id) + "_" + componentIndex.ToString(CultureInfo.InvariantCulture));
                slider.MappedValueChanged += (_, args) =>
                {
                    if (_updatingAddonControlValues)
                        return;

                    textBox.Text = FormatValue(args.Value);
                    QueueAddonControlUpdate(control, BuildValue());
                };
                row.Children.Add(slider);
            }
            else
            {
                row.Children.Add(new TextBlock
                {
                    Text = control.Kind,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            textBox.KeyDown += async (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                    await CommitValueAsync();
            };
            textBox.LostFocus += async (_, _) => await CommitValueAsync();

            Grid.SetColumn(textBox, 1);
            row.Children.Add(textBox);
            panel.Children.Add(row);
            editors.Add((slider, textBox));
        }

        RegisterAddonControlValueUpdater(control.Id, updated =>
        {
            var updatedValues = updated.NumericValues;
            for (var component = 0; component < editors.Count; ++component)
            {
                var editor = editors[component];
                var updatedValue = component < updatedValues.Count
                    ? updatedValues[component]
                    : component == 0 ? updated.NumericValue : 0.0;

                if (editor.TextBox.FocusState == FocusState.Unfocused)
                    editor.TextBox.Text = FormatValue(updatedValue);

                if (editor.Slider == null || editor.Slider.IsDragging)
                    continue;

                if (!hasExplicitRange && (updatedValue < editor.Slider.MappedMinimum || updatedValue > editor.Slider.MappedMaximum))
                {
                    var dynamicSpan = Math.Max(1.0, Math.Abs(updatedValue) * 2.0);
                    editor.Slider.ConfigureRange(updatedValue - dynamicSpan, updatedValue + dynamicSpan, updatedValue, isInteger ? 1.0 : 0.001);
                }
                else
                {
                    editor.Slider.SetMappedValue(updatedValue, false);
                }
            }
        });

        return panel;
    }

    private void RegisterAddonControlValueUpdater(string id, Action<AddonImGuiControlViewModel> updater)
    {
        _addonControlValueUpdaters[id] = updater;
    }

    private void UpdateAddonControlValues()
    {
        _updatingAddonControlValues = true;
        try
        {
            foreach (var control in ViewModel.AddonImGuiControls)
            {
                if (_addonControlValueUpdaters.TryGetValue(control.Id, out var updater))
                    updater(control);
            }
        }
        finally
        {
            _updatingAddonControlValues = false;
        }
    }

    private void QueueAddonControlUpdate(AddonImGuiControlViewModel control, string value)
    {
        if (_addonControlUpdateSources.TryGetValue(control.Id, out var oldSource))
            oldSource.Cancel();

        var source = new CancellationTokenSource();
        _addonControlUpdateSources[control.Id] = source;
        _ = SendAddonControlUpdateAsync(control, value, source);
    }

    private static void ApplyAddonAutomation(FrameworkElement element, AddonImGuiControlViewModel control)
    {
        AutomationProperties.SetAutomationId(element, "AddonControl_" + MakeAutomationId(control.Id));
        AutomationProperties.SetName(element, control.Label);
    }

    private async Task SendAddonControlUpdateAsync(AddonImGuiControlViewModel control, string value, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(16, source.Token);
            await ViewModel.SetAddonImGuiValueAsync(control, value);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_addonControlUpdateSources.TryGetValue(control.Id, out var current) && ReferenceEquals(current, source))
                _addonControlUpdateSources.Remove(control.Id);
            source.Dispose();
        }
    }

    private static bool TryParseFirstNumber(string text, out double value)
    {
        foreach (var token in text.Split(new[] { ',', ';', ' ', '\t', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
        }

        value = 0.0;
        return false;
    }

    private static string MakeAutomationId(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return builder.ToString();
    }

    private FrameworkElement CreateEffectHeader(EffectControlViewModel effect)
    {
        var headerGrid = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var gripIcon = new TextBlock
        {
            Text = "\uE700",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var grip = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(1, 255, 255, 255)),
            Child = gripIcon,
            Tag = effect.Name,
            CanDrag = true
        };
        grip.DragStarting += OnEffectDragStarting;
        grip.Tapped += (_, args) => args.Handled = true;
        ToolTipService.SetToolTip(grip, "Drag to reorder");
        headerGrid.Children.Add(grip);

        var title = new TextBlock
        {
            Text = effect.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 1);
        headerGrid.Children.Add(title);

        return headerGrid;
    }

    private void OnEffectDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: string effectName })
            return;

        _draggedEffectName = effectName;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText(effectName);
    }

    private void OnEffectDragOver(object sender, DragEventArgs args)
    {
        if (!string.IsNullOrEmpty(_draggedEffectName))
            args.AcceptedOperation = DataPackageOperation.Move;
        args.Handled = true;
    }

    private async void OnEffectDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        if (sender is not FrameworkElement { Tag: string targetEffectName } target ||
            string.IsNullOrEmpty(_draggedEffectName) ||
            string.Equals(_draggedEffectName, targetEffectName, StringComparison.OrdinalIgnoreCase))
        {
            _draggedEffectName = null;
            return;
        }

        var draggedEffectName = _draggedEffectName;
        _draggedEffectName = null;
        var insertAfter = args.GetPosition(target).Y > target.ActualHeight * 0.5;

        var orderedEffectNames = ViewModel.MoveEffect(draggedEffectName, targetEffectName, insertAfter);
        BuildControls();
        await RunUiCommandAsync(() => ViewModel.ReorderEnabledEffectsAsync(orderedEffectNames));
    }

    private void UpdateControlsPanelWidth()
    {
        var width = Math.Max(0.0, ControlsScrollViewer.ActualWidth - 24.0);
        if (width > 0.0)
            ControlsPanel.Width = width;
    }

    private Expander CreateSectionExpander(object header, bool isExpanded, UIElement content, Thickness? contentMargin = null)
    {
        UIElement expanderContent = content;
        if (contentMargin.HasValue)
        {
            var margin = contentMargin.Value;
            var contentGrid = new Grid
            {
                Margin = margin,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            contentGrid.Loaded += (_, _) => UpdateCompensatedContentWidth(contentGrid, margin);
            contentGrid.SizeChanged += (_, _) => UpdateCompensatedContentWidth(contentGrid, margin);
            contentGrid.Children.Add(content);
            expanderContent = contentGrid;
        }

        return new Expander
        {
            Header = header,
            IsExpanded = isExpanded,
            Content = expanderContent,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private static void UpdateCompensatedContentWidth(FrameworkElement element, Thickness margin)
    {
        if (element.Parent is not FrameworkElement parent || parent.ActualWidth <= 0.0)
            return;

        const double rightPadding = 48.0;
        var leftCompensation = Math.Max(0.0, -margin.Left);
        var width = parent.ActualWidth + leftCompensation - rightPadding;
        if (width > 0.0)
            element.Width = width;
    }

    private FrameworkElement CreateAddonNestedSectionExpander(
        AddonImGuiControlViewModel control,
        UIElement content,
        string automationPrefix)
    {
        var contentHost = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            Child = content,
            Visibility = control.IsOpen ? Visibility.Visible : Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var chevron = new TextBlock
        {
            Text = control.IsOpen ? "\uE70E" : "\uE70D",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 12
        };

        var headerGrid = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(new TextBlock
        {
            Text = control.Label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(chevron, 1);
        headerGrid.Children.Add(chevron);

        var headerButton = new Button
        {
            Content = headerGrid,
            Padding = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = control
        };
        headerButton.Click += async (_, _) =>
        {
            var show = contentHost.Visibility != Visibility.Visible;
            contentHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = show ? "\uE70E" : "\uE70D";
            await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync(control, show ? "true" : "false"));
        };

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(headerButton);
        panel.Children.Add(contentHost);

        var result = new Border
        {
            Margin = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 52, 52, 52)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 64, 64, 64)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = control
        };
        ApplyAddonAutomation(result, control);
        AutomationProperties.SetAutomationId(result, automationPrefix + MakeAutomationId(control.Id));
        return result;
    }

    private static FrameworkElement CreateNestedSectionExpander(object header, bool isExpanded, UIElement content)
    {
        var contentHost = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            Child = content,
            Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var chevron = new TextBlock
        {
            Text = isExpanded ? "\uE70E" : "\uE70D",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 12
        };

        var headerGrid = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(new TextBlock
        {
            Text = header?.ToString() ?? string.Empty,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(chevron, 1);
        headerGrid.Children.Add(chevron);

        var headerButton = new Button
        {
            Content = headerGrid,
            Padding = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        headerButton.Click += (_, _) =>
        {
            var show = contentHost.Visibility != Visibility.Visible;
            contentHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = show ? "\uE70E" : "\uE70D";
        };

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(headerButton);
        panel.Children.Add(contentHost);

        return new Border
        {
            Margin = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 52, 52, 52)),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 64, 64, 64)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
    }

    private FrameworkElement BuildEffectPanel(EffectControlViewModel effect)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };

        if (effect.PreprocessorDefinitions.Count != 0)
        {
            var definitionsPanel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var definition in effect.PreprocessorDefinitions)
                definitionsPanel.Children.Add(BuildPreprocessorEditor(definition));

            panel.Children.Add(CreateNestedSectionExpander("Preprocessor Definitions", false, definitionsPanel));
        }

        foreach (var group in effect.Uniforms.GroupBy(static uniform => uniform.Category))
        {
            var groupPanel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var uniform in group)
                groupPanel.Children.Add(BuildUniformEditor(uniform));

            panel.Children.Add(CreateNestedSectionExpander(group.Key, true, groupPanel));
        }

        return panel;
    }

    private FrameworkElement BuildPreprocessorEditor(PreprocessorDefinitionViewModel definition)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = definition.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var row = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textBox = new TextBox { Text = definition.Value, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
        var button = new Button { Content = "Apply", Tag = definition };
        button.Click += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetPreprocessorDefinitionAsync((PreprocessorDefinitionViewModel)button.Tag, textBox.Text));
        row.Children.Add(textBox);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        panel.Children.Add(row);
        if (!string.IsNullOrEmpty(definition.DefaultValue))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Default: " + definition.DefaultValue,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap
            });
        }
        return panel;
    }

    private FrameworkElement CreateMappedSliderLabel(string text, ICollection<FrameworkElement> indicatorCollection)
    {
        var label = new TextBlock
        {
            Text = text,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var indicator = new TextBlock
        {
            Text = "L",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = ViewModel.Settings.SliderSymLog ? Visibility.Visible : Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(indicator, "Symmetric logarithmic scale");
        AutomationProperties.SetName(indicator, "Symmetric logarithmic scale enabled");
        indicatorCollection.Add(indicator);

        var header = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(label);
        Grid.SetColumn(indicator, 1);
        header.Children.Add(indicator);
        return header;
    }

    private FrameworkElement BuildUniformEditor(UniformViewModel uniform)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        var hasChoiceEditor = (uniform.UiType == "combo" || uniform.UiType == "list" || uniform.UiType == "radio") && uniform.Items.Length != 0;
        var hasNumericSlider = !hasChoiceEditor && uniform.Type != "bool";
        panel.Children.Add(hasNumericSlider
            ? CreateMappedSliderLabel(uniform.Label, _fxLogIndicators)
            : new TextBlock { Text = uniform.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        if (hasChoiceEditor)
        {
            var combo = new ComboBox { MinWidth = 180, Tag = uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var item in uniform.Items)
                combo.Items.Add(item);
            combo.SelectedIndex = Math.Clamp(ToInt(uniform.Values.FirstOrDefault()), 0, Math.Max(0, combo.Items.Count - 1));
            combo.SelectionChanged += async (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                    await RunUiCommandAsync(() => ViewModel.SetUniformAsync((UniformViewModel)combo.Tag, combo.SelectedIndex));
            };
            panel.Children.Add(combo);
            return panel;
        }

        if (uniform.Type == "bool")
        {
            var checkBox = new CheckBox { Content = uniform.EffectName, IsChecked = ToBool(uniform.Values.FirstOrDefault()), Tag = uniform };
            checkBox.Checked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetUniformAsync((UniformViewModel)checkBox.Tag, true));
            checkBox.Unchecked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetUniformAsync((UniformViewModel)checkBox.Tag, false));
            panel.Children.Add(checkBox);
            return panel;
        }

        var numericValues = uniform.Values.Length == 0 ? new[] { 0.0 } : uniform.Values.Select(ToDouble).ToArray();
        var textBoxes = new List<TextBox>();
        var sliders = new List<MappedSlider>();
        for (var i = 0; i < numericValues.Length; ++i)
        {
            var componentIndex = i;
            var row = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });

            var isSlider = string.Equals(uniform.UiType, "slider", StringComparison.OrdinalIgnoreCase);
            var minimum = uniform.Minimum ?? (isSlider ? 0.0 : numericValues[i] - Math.Max(1.0, Math.Abs(numericValues[i]) * 2.0));
            var maximum = uniform.Maximum ?? (isSlider ? 1.0 : numericValues[i] + Math.Max(1.0, Math.Abs(numericValues[i]) * 2.0));
            if (Math.Abs(maximum - minimum) < 0.000001)
                maximum = minimum + 1.0;

            var step = uniform.Step ?? (uniform.Type == "float" ? 0.001 : 1.0);
            var slider = new MappedSlider
            {
                Tag = uniform,
                MinWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            slider.ConfigureRange(minimum, maximum, numericValues[i], step);
            slider.SetInteractionMode(ViewModel.Settings.SliderDragSensitivity, ViewModel.Settings.SliderSymLog);
            slider.DragSensitivityWheelChanged += OnSliderDragSensitivityWheelChanged;
            _fxMappedSliders.Add(slider);
            var textBox = new TextBox { Text = numericValues[i].ToString("0.######", CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Stretch };
            textBoxes.Add(textBox);
            sliders.Add(slider);
            slider.MappedValueChanged += (_, args) =>
            {
                textBox.Text = args.Value.ToString("0.######", CultureInfo.InvariantCulture);
                QueueUniformUpdate(uniform, BuildNumericValue(textBoxes, componentIndex, args.Value));
            };
            textBox.KeyDown += async (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    ApplyTextValuesToSliders(textBoxes, sliders);
                    if (TryBuildNumericValue(textBoxes, out var value))
                        await RunUiCommandAsync(() => ViewModel.SetUniformAsync(uniform, value));
                }
            };
            textBox.LostFocus += async (_, _) =>
            {
                ApplyTextValuesToSliders(textBoxes, sliders);
                if (TryBuildNumericValue(textBoxes, out var value))
                    await RunUiCommandAsync(() => ViewModel.SetUniformAsync(uniform, value));
            };

            row.Children.Add(slider);
            Grid.SetColumn(textBox, 1);
            row.Children.Add(textBox);
            panel.Children.Add(row);
        }

        return panel;
    }

    private static void ApplyTextValuesToSliders(IReadOnlyList<TextBox> boxes, IReadOnlyList<MappedSlider> sliders)
    {
        for (var i = 0; i < boxes.Count && i < sliders.Count; ++i)
        {
            if (!double.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;

            sliders[i].SetMappedValue(value, false);
            boxes[i].Text = sliders[i].MappedValue.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    private void QueueUniformUpdate(UniformViewModel uniform, object value)
    {
        if (_uniformUpdateSources.TryGetValue(uniform.Id, out var oldSource))
            oldSource.Cancel();

        var source = new CancellationTokenSource();
        _uniformUpdateSources[uniform.Id] = source;
        _ = SendUniformUpdateAsync(uniform, value, source);
    }

    private async Task SendUniformUpdateAsync(UniformViewModel uniform, object value, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(16, source.Token);
            await ViewModel.SetUniformAsync(uniform, value);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_uniformUpdateSources.TryGetValue(uniform.Id, out var current) && ReferenceEquals(current, source))
                _uniformUpdateSources.Remove(uniform.Id);
            source.Dispose();
        }
    }

    private static object BuildNumericValue(IReadOnlyList<TextBox> boxes, int changedIndex, double changedValue)
    {
        var values = new double[boxes.Count];
        for (var i = 0; i < values.Length; ++i)
        {
            if (i == changedIndex)
                values[i] = changedValue;
            else if (!double.TryParse(boxes[i].Text, out values[i]))
                values[i] = 0;
        }

        return values.Length == 1 ? values[0] : values;
    }

    private static bool TryBuildNumericValue(IReadOnlyList<TextBox> boxes, out object value)
    {
        var values = new double[boxes.Count];
        for (var i = 0; i < values.Length; ++i)
        {
            if (!double.TryParse(boxes[i].Text, out values[i]))
            {
                value = 0.0;
                return false;
            }
        }

        value = values.Length == 1 ? values[0] : values;
        return true;
    }

    private static bool ToBool(object? value) => value is bool boolValue ? boolValue : ToDouble(value) != 0;
    private static int ToInt(object? value) => (int)Math.Round(ToDouble(value));
    private static double ToDouble(object? value)
    {
        return value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            bool boolValue => boolValue ? 1.0 : 0.0,
            string stringValue when double.TryParse(stringValue, out var parsed) => parsed,
            _ => 0.0
        };
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
            ContentDialog dialog = new()
            {
                Title = "Offline ReShade",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
            return false;
        }
    }
}
