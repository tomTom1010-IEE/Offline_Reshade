using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using System.Globalization;
using OfflineReShade.WinUI.Services;
using OfflineReShade.WinUI.ViewModels;
using WinRT.Interop;

namespace OfflineReShade.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, CancellationTokenSource> _uniformUpdateSources = new();
    private readonly Dictionary<string, CancellationTokenSource> _addonControlUpdateSources = new();
    private readonly PreviewHostService _previewHost;
    private readonly PreviewHostService _addonOverlayHost;
    private readonly D3DPreviewBridge _d3dPreview = new();
    private readonly DispatcherTimer _gpuPreviewTimer = new();
    private bool _isPreviewDragging;
    private bool _updatingInputModeSwitch;
    private string? _draggedEffectName;
    private Point _lastPreviewDragPoint;
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
        _addonOverlayHost = new PreviewHostService(windowHandle, AddonNativeSurface);
        ViewModel.Initialize(new SettingsPickerService(() => windowHandle), () => _previewHost.EnsureHandle(), () => _addonOverlayHost.EnsureHandle());
        ViewModel.ControlsChanged += BuildControls;
        ViewModel.AddonControlsChanged += BuildAddonControls;
        ViewModel.PreviewFrameReceived += frame => PreviewImage.Source = frame;
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
                UpdateContentView();
            if (args.PropertyName == nameof(MainWindowViewModel.IsPreviewRunning))
                UpdatePreviewTransportView();
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
        _gpuPreviewTimer.Interval = TimeSpan.FromMilliseconds(16);
        _gpuPreviewTimer.Tick += (_, _) => RenderGpuPreviewFrame();
        Closed += (_, _) =>
        {
            if (_previewXamlRoot is not null)
                _previewXamlRoot.Changed -= OnPreviewXamlRootChanged;
            _previewHost.Dispose();
            _addonOverlayHost.Dispose();
            _d3dPreview.Dispose();
            ViewModel.AddonControlsChanged -= BuildAddonControls;
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

    private void UpdateAddonOverlayHostVisibility()
    {
        _addonOverlayHost.SetVisible(!ViewModel.IsSettingsOpen && ControlsTabView.SelectedIndex == 1);
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

    private void BuildControls()
    {
        ControlsPanel.Children.Clear();
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

        if (ViewModel.Addons.Count == 0)
        {
            AddonsPanel.Children.Add(new TextBlock
            {
                Text = "No add-ons are loaded. Check AddonPath in OfflinePrototype\\ReShade.ini and restart preview.",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        var nativePanelButton = new Button
        {
            Content = "Open native add-on panel",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        nativePanelButton.Click += async (_, _) => await RunUiCommandAsync(ViewModel.OpenNativeAddonPanelAsync);
        AddonsPanel.Children.Add(nativePanelButton);
        AddonsPanel.Children.Add(new TextBlock
        {
            Text = "Use this fallback for complex add-on UI that cannot be represented by standard WinUI controls yet.",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var addon in ViewModel.Addons)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = addon.StatusText + " - " + addon.OverlayText,
                Foreground = new SolidColorBrush(addon.IsLoaded ? Microsoft.UI.Colors.LightGreen : Microsoft.UI.Colors.Orange),
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(addon.SourceText))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = addon.SourceText,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
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

            foreach (var overlay in addon.Overlays)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Overlay: " + overlay,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            AddonsPanel.Children.Add(CreateSectionExpander(addon.Name, false, panel));
        }
    }

    private void BuildAddonControls()
    {
        AddonControlsPanel.Children.Clear();

        if (ViewModel.AddonImGuiControls.Count == 0)
        {
            AddonControlsPanel.Children.Add(new TextBlock
            {
                Text = "No standard add-on ImGui controls captured yet. Complex custom draw-list UI still needs the native panel fallback.",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var group in ViewModel.AddonImGuiControls.GroupBy(static control => control.GroupKey))
        {
            var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var control in group)
                panel.Children.Add(BuildAddonControlEditor(control));

            AddonControlsPanel.Children.Add(CreateSectionExpander(group.Key, true, panel));
        }
    }

    private FrameworkElement BuildAddonControlEditor(AddonImGuiControlViewModel control)
    {
        if (control.IsButton)
        {
            var button = new Button
            {
                Content = control.Label,
                Tag = control,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
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

    private FrameworkElement BuildAddonComboEditor(AddonImGuiControlViewModel control)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = control.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        var combo = new ComboBox
        {
            Tag = control,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
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
        panel.Children.Add(new TextBlock { Text = control.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        var row = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

        var numericValue = control.NumericValue;
        var hasMinimum = TryParseFirstNumber(control.Minimum, out var minimum);
        var hasMaximum = TryParseFirstNumber(control.Maximum, out var maximum);
        var hasRange = hasMinimum &&
            hasMaximum &&
            maximum > minimum;

        var textBox = new TextBox
        {
            Text = control.Value,
            Tag = control,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        if (hasRange && control.Components <= 1 && !control.IsCombo)
        {
            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Clamp(numericValue, minimum, maximum),
                StepFrequency = control.Kind.Contains("int", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.001,
                Tag = control,
                MinWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            slider.ValueChanged += (_, _) =>
            {
                var value = slider.Value.ToString("0.######", CultureInfo.InvariantCulture);
                textBox.Text = value;
                QueueAddonControlUpdate(control, value);
            };
            row.Children.Add(slider);
        }
        else
        {
            row.Children.Add(new TextBlock
            {
                Text = control.Kind,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
        }

        textBox.KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
                await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync(control, textBox.Text));
        };
        textBox.LostFocus += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync(control, textBox.Text));

        Grid.SetColumn(textBox, 1);
        row.Children.Add(textBox);
        panel.Children.Add(row);
        return panel;
    }

    private void QueueAddonControlUpdate(AddonImGuiControlViewModel control, string value)
    {
        if (_addonControlUpdateSources.TryGetValue(control.Id, out var oldSource))
            oldSource.Cancel();

        var source = new CancellationTokenSource();
        _addonControlUpdateSources[control.Id] = source;
        _ = SendAddonControlUpdateAsync(control, value, source);
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

    private FrameworkElement BuildUniformEditor(UniformViewModel uniform)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = uniform.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        if ((uniform.UiType == "combo" || uniform.UiType == "list" || uniform.UiType == "radio") && uniform.Items.Length != 0)
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
        var sliders = new List<Slider>();
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

            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Clamp(numericValues[i], minimum, maximum),
                StepFrequency = uniform.Step ?? (uniform.Type == "float" ? 0.001 : 1.0),
                Tag = uniform,
                MinWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var textBox = new TextBox { Text = numericValues[i].ToString("0.######"), HorizontalAlignment = HorizontalAlignment.Stretch };
            textBoxes.Add(textBox);
            sliders.Add(slider);
            slider.ValueChanged += (_, _) =>
            {
                textBox.Text = slider.Value.ToString("0.######");
                QueueUniformUpdate(uniform, BuildNumericValue(textBoxes, componentIndex, slider.Value));
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

    private static void ApplyTextValuesToSliders(IReadOnlyList<TextBox> boxes, IReadOnlyList<Slider> sliders)
    {
        for (var i = 0; i < boxes.Count && i < sliders.Count; ++i)
        {
            if (!double.TryParse(boxes[i].Text, out var value))
                continue;

            sliders[i].Value = Math.Clamp(value, sliders[i].Minimum, sliders[i].Maximum);
            boxes[i].Text = sliders[i].Value.ToString("0.######");
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

    private async Task RunUiCommandAsync(Func<Task> action)
    {
        try
        {
            await action();
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
        }
    }
}
