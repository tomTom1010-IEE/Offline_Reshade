using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using System.Globalization;
using System.Text.Json.Nodes;
using OfflineReShade.Core;
using OfflineReShade.WinUI.Controls;
using OfflineReShade.WinUI.Services;
using OfflineReShade.WinUI.ViewModels;
using WinRT.Interop;

namespace OfflineReShade.WinUI.Views;

public sealed partial class EffectsPanel : UserControl, IDisposable
{
    private const string ChevronDown = "\uE70D", ChevronUp = "\uE70E", ChevronRight = "\uE76C";
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);
    private async void OnRefreshServices(object sender, RoutedEventArgs e)
    {
        await RunUiCommandAsync(async () => {
            var result = await ViewModel.ReadAddonServicesAsync();
            AddonServicesPanel.Children.Clear();
            foreach (var service in result.GetProperty("services").EnumerateArray()) {
                var addon = service.GetProperty("addon").GetString()!;
                var group = new StackPanel { Spacing = 8 };
                group.Children.Add(new TextBlock { Text = addon, TextWrapping = TextWrapping.Wrap });
                if (service.GetProperty("error").GetString() is { Length: > 0 } error) { group.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap }); }
                else {
                    var schema = service.GetProperty("schema");
                    if (schema.TryGetProperty("scope", out var scope) && scope.GetString() == "companion_fx") group.Children.Add(new TextBlock { Text = "Companion FX parameters only. Private Add-on controls require a supported business interface.", TextWrapping = TextWrapping.Wrap });
                    if (schema.TryGetProperty("privateAdapterError", out var adapterError) && adapterError.GetString() is { Length: > 0 } reason)
                        group.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
                    foreach (var p in schema.GetProperty("parameters").EnumerateArray()) {
                        var node = JsonNode.Parse(p.GetRawText())!.AsObject();
                        node["addonService"] = addon; node["effectName"] = addon; node["name"] = node["id"]!.GetValue<string>();
                        var uniform = JsonStateParser.ParseUniforms(AutomationEngine.Json(new { uniforms = new[] { node } }))[0];
                        group.Children.Add(BuildUniformEditor(uniform));
                    }
                    if (schema.TryGetProperty("actions", out var actions)) foreach (var action in actions.EnumerateArray()) {
                        var id = action.GetProperty("id").GetString()!;
                        if (action.TryGetProperty("requiresArguments", out var required) && required.GetBoolean()) {
                            group.Children.Add(new TextBlock { Text = $"{id} — use MCP invoke_addon_action with the declared arguments.", TextWrapping = TextWrapping.Wrap });
                            continue;
                        }
                        var button = new Button { Content = action.TryGetProperty("label", out var label) ? label.GetString() : id };
                        AutomationProperties.SetAutomationId(button, "ServiceAction_" + MakeAutomationId(addon + id));
                        button.Click += async (_, _) => await RunUiCommandAsync(() => ViewModel.InvokeAddonActionAsync(addon, id));
                        group.Children.Add(button);
                    }
                }
                AddonServicesPanel.Children.Add(group);
            }
            if (AddonServicesPanel.Children.Count == 0) AddonServicesPanel.Children.Add(new TextBlock { Text = "No independent service available for the loaded Add-ons.", TextWrapping = TextWrapping.Wrap });
        });
    }
    private readonly HashSet<string> _expandedEffects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _toggledSections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FrameworkElement, (List<MappedSlider> Sliders, List<FrameworkElement> Indicators)> _effectEditors = new();
    private List<string> _activeEffectOrder = new();
    private string _effectQuery = string.Empty;
    private bool _updatingMasterSwitch;
    private readonly DispatcherTimer _effectSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Dictionary<string, Action<AddonImGuiControlViewModel>> _addonControlValueUpdaters = new();
    private readonly List<MappedSlider> _fxMappedSliders = new();
    private readonly List<(FrameworkElement Owner, Func<Task> Commit)> _uniformTextCommits = new();
    private long _controlEpoch;
    private readonly List<MappedSlider> _addonMappedSliders = new();
    private readonly List<FrameworkElement> _fxLogIndicators = new();
    private readonly List<FrameworkElement> _addonLogIndicators = new();
    private readonly D3DPreviewBridge _addonOverlayPreview = new();
    private readonly DispatcherTimer _addonOverlayPreviewTimer = new();
    private readonly DispatcherTimer _addonInputMoveTimer = new();
    private bool _isClosing;
    private int _addonGpuFailures;
    private DateTimeOffset _addonRetryAt;
    private bool _addonGpuFailed;
    private bool _buildingAddonOverlayTabs;
    private bool _updatingAddonControlValues;
    private string _activeNativeAddonOverlayId = string.Empty;
    private string? _draggedEffectName;
    private PreviewHostInputMessage? _pendingAddonMouseMove;
    private uint? _capturedAddonPointerId;
    private uint _lastRequestedAddonOverlayWidth;
    private uint _lastRequestedAddonOverlayHeight;

    private bool _paneVisible = true;
    private XamlRoot? _root;
    public event Action<string>? SliderModeChanged;
    public MainWindowViewModel ViewModel { get; }
    private double AddonOverlayRasterizationScale => AddonNativeSurface.XamlRoot?.RasterizationScale ?? 1;
    public EffectsPanel(MainWindowViewModel vm)
    {
        ViewModel = vm; InitializeComponent();
        _effectSearchTimer.Tick += (_, _) => { _effectSearchTimer.Stop(); FilterEffects(); };
        _addonOverlayPreviewTimer.Interval = TimeSpan.FromMilliseconds(16);
        _addonOverlayPreviewTimer.Tick += (_, _) => RenderAddonOverlayFrame();
        _addonInputMoveTimer.Interval = TimeSpan.FromMilliseconds(16);
        _addonInputMoveTimer.Tick += (_, _) => FlushAddonOverlayMouseMove();
        ViewModel.ControlsChanged += BuildControls;
        ViewModel.AddonControlsChanged += BuildAddonControls;
        ViewModel.AddonControlValuesChanged += UpdateAddonControlValues;
        ViewModel.SubmitVisualEdits += FlushVisibleEdits;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Settings.PropertyChanged += OnSettingsChanged;
        NativeAddonOverlaySwitch.IsOn = true;
        Loaded += (_, _) =>
        {
            if (_root != null) _root.Changed -= OnRootChanged;
            _root = XamlRoot;
            if (_root != null) _root.Changed += OnRootChanged;
        };
        BuildControls();
    }
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        ResizeAddonOverlayBridge();
        _ = SyncAddonOverlaySurfaceSizeAsync();
    }
    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.SliderDragSensitivity) or nameof(SettingsViewModel.SliderSymLog)) UpdateSliderInteractionMode();
    }
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_isClosing) return;
        if (args.PropertyName == nameof(MainWindowViewModel.IsPreviewRunning))
        {
            if (!ViewModel.IsPreviewRunning) { _addonOverlayPreview.Dispose(); _addonGpuFailures = 0; _addonRetryAt = DateTimeOffset.MinValue; _addonGpuFailed = false; }
            _activeNativeAddonOverlayId = string.Empty;
        }
        if (args.PropertyName is nameof(MainWindowViewModel.IsEffectsLoading) or nameof(MainWindowViewModel.IsPreviewRunning)) UpdateEffectsState();
        if (args.PropertyName == nameof(MainWindowViewModel.SharedAddonOverlayHandle)) { _lastRequestedAddonOverlayWidth = _lastRequestedAddonOverlayHeight = 0; }
        if (args.PropertyName is nameof(MainWindowViewModel.IsSettingsOpen) or nameof(MainWindowViewModel.IsPreviewRunning) or nameof(MainWindowViewModel.SharedAddonOverlayHandle) or nameof(MainWindowViewModel.SharedAddonOverlayWidth) or nameof(MainWindowViewModel.SharedAddonOverlayHeight)) UpdateAddonOverlayHostVisibility();
    }
    public void SetPaneVisible(bool visible) { _paneVisible = visible; UpdateAddonOverlayHostVisibility(); }
    private void OnHidePanel(object sender, RoutedEventArgs args) => ViewModel.Settings.EffectsPaneOpen = false;
    public void FocusSearch() => EffectSearchBox.Focus(FocusState.Keyboard);
    public bool IsNativeFocused => XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), AddonOverlaySwapChainPanel);
    public void ToggleLogMode()
    {
        if (_fxMappedSliders.Concat(_addonMappedSliders).Any(x => x.IsDragging)) return;
        ViewModel.Settings.SliderSymLog = !ViewModel.Settings.SliderSymLog;
        SliderModeChanged?.Invoke(ViewModel.Settings.SliderSymLog ? "L  SYMLOG" : "LINEAR");
    }
    private void TryPreviewGraphics(Action render)
    {
        if (_isClosing || !_paneVisible || _addonGpuFailed || DateTimeOffset.UtcNow < _addonRetryAt) return;
        try { render(); _addonGpuFailures = 0; }
        catch (Exception ex)
        {
            _addonOverlayPreview.Dispose(); var attempts = ++_addonGpuFailures;
            _addonRetryAt = DateTimeOffset.UtcNow.AddMilliseconds(400);
            ViewModel.ReportPreviewError($"Add-on preview rebuild {attempts}/3: {ex.Message} (0x{ex.HResult:X8})");
            if (attempts >= 3) { _addonGpuFailed = true; UpdateAddonOverlayHostVisibility(); }
        }
    }
    public void Dispose()
    {
        _isClosing = true;
        if (_root != null) _root.Changed -= OnRootChanged;
        _effectSearchTimer.Stop(); _addonInputMoveTimer.Stop(); _addonOverlayPreviewTimer.Stop(); _addonOverlayPreview.Dispose();
        ViewModel.ControlsChanged -= BuildControls; ViewModel.AddonControlsChanged -= BuildAddonControls;
        ViewModel.AddonControlValuesChanged -= UpdateAddonControlValues; ViewModel.SubmitVisualEdits -= FlushVisibleEdits;
        ViewModel.PropertyChanged -= OnViewModelChanged; ViewModel.Settings.PropertyChanged -= OnSettingsChanged;
    }
    private bool IsAddonsPageSelected => PanelSelector?.SelectedItem is { } selected && ReferenceEquals(selected, AddonsPageItem);
    private void OnPanelSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (EffectsPage == null || AddonsPage == null || NativeCompatibility == null) return;
        var addons = IsAddonsPageSelected;
        EffectsPage.Visibility = addons ? Visibility.Collapsed : Visibility.Visible;
        AddonsPage.Visibility = addons ? Visibility.Visible : Visibility.Collapsed;
        UpdateAddonOverlayHostVisibility();
    }

    private void OnAddonOverlayTabSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_buildingAddonOverlayTabs)
            UpdateAddonOverlayHostVisibility();
    }

    private void OnNativeCompatibilityChanged(Expander sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_isClosing) UpdateAddonOverlayHostVisibility();
    });

    private void OnNativeAddonOverlayToggled(object sender, RoutedEventArgs args)
    {
        UpdateAddonOverlayHostVisibility();
    }

    private void UpdateAddonOverlayHostVisibility()
    {
        var selectedOverlay = (AddonOverlayTabView.SelectedItem as TabViewItem)?.Tag as AddonOverlayViewModel;
        var visible = !_addonGpuFailed && ViewModel.IsPreviewRunning &&
                      !ViewModel.IsSettingsOpen && _paneVisible &&
                      IsAddonsPageSelected && NativeCompatibility.IsExpanded &&
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
                    : _addonGpuFailed ? "GPU add-on overlay unavailable; restart preview to retry" : "Preparing native add-on overlay";

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

        FlushAddonOverlayMouseMove();
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
        var sx = ViewModel.SharedAddonOverlayWidth / Math.Max(1.0, AddonOverlaySwapChainPanel.ActualWidth);
        var sy = ViewModel.SharedAddonOverlayHeight / Math.Max(1.0, AddonOverlaySwapChainPanel.ActualHeight);
        var x = Math.Clamp((int)Math.Round(point.X * sx), 0, ushort.MaxValue);
        var y = Math.Clamp((int)Math.Round(point.Y * sy), 0, ushort.MaxValue);
        return (long)(uint)(x | (y << 16));
    }

    private void OnSliderDragSensitivityWheelChanged(object? sender, MappedSliderWheelEventArgs args)
    {
        var factors = new[] { 1, 2, 4, 8 };
        var currentIndex = Array.IndexOf(factors, ViewModel.Settings.SliderDragSensitivity);
        if (currentIndex < 0)
            currentIndex = 0;
        var nextIndex = Math.Clamp(currentIndex + (args.Delta > 0 ? 1 : -1), 0, factors.Length - 1);
        ViewModel.Settings.SliderDragSensitivity = factors[nextIndex];
        SliderModeChanged?.Invoke("x" + factors[nextIndex].ToString(CultureInfo.InvariantCulture));
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

    private void EnsureAddonOverlayBridge() => TryPreviewGraphics(EnsureAddonOverlayBridgeCore);
    private void EnsureAddonOverlayBridgeCore()
    {
        if (_isClosing || !ViewModel.IsPreviewRunning ||
            ViewModel.IsSettingsOpen ||
            !IsAddonsPageSelected || !NativeCompatibility.IsExpanded ||
            !NativeAddonOverlaySwitch.IsOn ||
            ViewModel.SharedAddonOverlayHandle == 0 ||
            ViewModel.SharedAddonOverlayWidth == 0 ||
            ViewModel.SharedAddonOverlayHeight == 0)
            return;

        var (width, height) = PreviewGeometry.Surface(AddonNativeSurface.ActualWidth, AddonNativeSurface.ActualHeight, AddonOverlayRasterizationScale);
        _addonOverlayPreview.EnsureCreated(AddonOverlaySwapChainPanel, width, height, ViewModel.AdapterLuid);
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

        TryPreviewGraphics(() => {
            var (width, height) = PreviewGeometry.Surface(AddonNativeSurface.ActualWidth, AddonNativeSurface.ActualHeight, AddonOverlayRasterizationScale);
            _addonOverlayPreview.Resize(width, height);
            _addonOverlayPreview.SetViewTransform(0.0, 0.0, width, height);
        });
    }

    private async Task SyncAddonOverlaySurfaceSizeAsync()
    {
        if (!ViewModel.IsPreviewRunning ||
            ViewModel.IsSettingsOpen ||
            !IsAddonsPageSelected || !NativeCompatibility.IsExpanded ||
            !NativeAddonOverlaySwitch.IsOn)
            return;

        var (width, height) = PreviewGeometry.Surface(AddonNativeSurface.ActualWidth, AddonNativeSurface.ActualHeight, AddonOverlayRasterizationScale);
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
        _controlEpoch++;
        _uniformTextCommits.Clear();
        _fxMappedSliders.Clear();
        _fxLogIndicators.Clear();
        _effectEditors.Clear();
        AddonsPanel.Children.Clear();
        AddonControlsPanel.Children.Clear();

        _updatingMasterSwitch = true;
        EffectsMasterSwitch.IsOn = ViewModel.EffectsEnabled;
        _updatingMasterSwitch = false;
        FilterEffects();

        BuildAddons();
        BuildAddonControls();
    }

    private async void OnEffectsMasterToggled(object sender, RoutedEventArgs args)
    {
        if (_updatingMasterSwitch || EffectsMasterSwitch.IsOn == ViewModel.EffectsEnabled) return;
        if (!await RunUiCommandAsync(() => ViewModel.SetEffectsEnabledAsync(EffectsMasterSwitch.IsOn)))
        {
            _updatingMasterSwitch = true;
            EffectsMasterSwitch.IsOn = ViewModel.EffectsEnabled;
            _updatingMasterSwitch = false;
        }
    }

    private void OnEffectSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { _effectSearchTimer.Stop(); _effectSearchTimer.Start(); }

    // Active effects keep their render order; available effects are sorted by name.
    private void FilterEffects()
    {
        if (EffectsList == null) return;
        var query = _effectQuery = EffectSearchBox.Text.Trim();
        var parameters = ViewModel.Effects.ToDictionary(static effect => effect.Name, StringComparer.OrdinalIgnoreCase);
        var techniques = ViewModel.Techniques.GroupBy(static technique => technique.EffectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<TechniqueViewModel>)group.ToArray(), StringComparer.OrdinalIgnoreCase);
        _activeEffectOrder = ViewModel.Techniques.Where(static technique => technique.IsEnabled)
            .Select(static technique => technique.EffectName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var activeNames = new HashSet<string>(_activeEffectOrder, StringComparer.OrdinalIgnoreCase);

        var active = _activeEffectOrder.Select(name => new EffectListItem(name, techniques[name], parameters.GetValueOrDefault(name)))
            .Where(item => item.Matches(query)).ToList();
        var available = techniques.Keys.Where(name => !activeNames.Contains(name)).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new EffectListItem(name, techniques[name], null)).Where(item => item.Matches(query)).ToList();

        var items = new List<EffectListItem>();
        if (active.Count != 0 || (query.Length == 0 && available.Count != 0))
            items.Add(EffectListItem.Section("Active", active.Count));
        if (active.Count == 0 && query.Length == 0 && available.Count != 0)
            items.Add(EffectListItem.Hint("No effects are on. Switch one on below."));
        items.AddRange(active);
        if (available.Count != 0)
        {
            items.Add(EffectListItem.Section("Available", available.Count));
            items.AddRange(available);
        }

        EffectsList.ItemsSource = items;
        UpdateEffectsState();
    }

    private void UpdateEffectsState()
    {
        if (EffectsList == null) return;
        EffectsMasterSwitch.IsEnabled = ViewModel.IsPreviewRunning && ViewModel.Techniques.Count != 0;
        var query = EffectSearchBox.Text.Trim();
        var hasRows = EffectsList.ItemsSource is IEnumerable<EffectListItem> items && items.Any(static item => item.Kind == EffectListItemKind.Effect);
        (string Title, string Message)? empty = hasRows ? null
            : ViewModel.Techniques.Count != 0 ? ($"No matches for “{query}”", "Search looks at effect, pass and parameter names.")
            : ViewModel.IsEffectsLoading ? ("Compiling effects…", "The first compile of a large effect folder can take a while.")
            : !ViewModel.IsPreviewRunning ? ("No image open", "Open an image or listen for game output to load effects.")
            : ("No effects found", "Add .fx files to the effects folder, or choose another folder in Settings.");
        EffectsEmptyState.Visibility = empty == null ? Visibility.Collapsed : Visibility.Visible;
        EffectsList.Visibility = empty == null ? Visibility.Visible : Visibility.Collapsed;
        if (empty is { } state) { EffectsEmptyTitle.Text = state.Title; EffectsEmptyMessage.Text = state.Message; }
    }

    private sealed class EffectRowParts(EffectListItem item)
    {
        public EffectListItem Item { get; } = item;
        public StackPanel? Parameters { get; set; }
        public FontIcon? Chevron { get; set; }
        public Button? ExpandButton { get; set; }
    }

    private void OnEffectRowDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) => BindEffectRow((ContentControl)sender);
    private void OnEffectRowLoaded(object sender, RoutedEventArgs args) => BindEffectRow((ContentControl)sender);
    private void OnEffectRowUnloaded(object sender, RoutedEventArgs args) => ClearEffectRow((ContentControl)sender);

    // List containers are recycled, so rows are rebuilt whenever their item changes.
    private void BindEffectRow(ContentControl host)
    {
        if (host.DataContext is not EffectListItem item) { ClearEffectRow(host); return; }
        if (ReferenceEquals(host.Tag, item) && host.Content != null) return;
        ClearEffectRow(host);
        host.Tag = item;
        host.Content = item.Kind switch
        {
            EffectListItemKind.Section => CreateSectionHeader(item),
            EffectListItemKind.Hint => new TextBlock { Text = item.Name, Style = (Style)Application.Current.Resources["SecondaryTextStyle"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 8) },
            _ => CreateEffectRow(item)
        };
    }

    private void ClearEffectRow(ContentControl host)
    {
        if (host.Content is FrameworkElement { Tag: EffectRowParts parts }) ReleaseEffect(parts.Parameters);
        host.Content = null;
        host.Tag = null;
    }

    private static FrameworkElement CreateSectionHeader(EffectListItem item)
    {
        var header = new TextBlock
        {
            Text = item.Name.ToUpperInvariant() + "   " + item.Count.ToString(CultureInfo.InvariantCulture),
            Style = (Style)Application.Current.Resources["SectionHeaderStyle"],
            Margin = new Thickness(4, 14, 0, 4)
        };
        AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
        return header;
    }

    private FrameworkElement CreateEffectRow(EffectListItem item)
    {
        var parts = new EffectRowParts(item);
        var row = new Grid { Background = TransparentBrush, Tag = parts };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { MinHeight = 40, ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(header);

        if (item.IsEnabled) header.Children.Add(CreateEffectGrip(item));

        var toggle = CreateCompactSwitch(item.IsEnabled);
        AutomationProperties.SetName(toggle, item.DisplayName);
        AutomationProperties.SetAutomationId(toggle, "EffectToggle_" + MakeAutomationId(item.Name));
        ToolTipService.SetToolTip(toggle, item.IsEnabled ? "Turn off " + item.DisplayName : "Turn on " + item.DisplayName);
        toggle.Toggled += async (_, _) =>
        {
            if (toggle.IsOn == item.IsEnabled) return;
            await RunUiCommandAsync(() => ViewModel.SetTechniquesStateAsync(item.Techniques, toggle.IsOn));
        };
        Grid.SetColumn(toggle, 1);
        header.Children.Add(toggle);

        // Styles resolve theme brushes per element; Application.Current.Resources would return the app theme's brush.
        var title = new TextBlock
        {
            Text = item.DisplayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!item.IsEnabled) title.Style = (Style)Application.Current.Resources["SecondaryTextStyle"];
        ToolTipService.SetToolTip(title, item.Name + Environment.NewLine + string.Join(Environment.NewLine, item.Techniques.Select(static technique => "• " + technique.Name)));
        Grid.SetColumn(title, 2);
        header.Children.Add(title);

        if (item.Techniques.Count > 1)
        {
            var passes = CreatePassesButton(item);
            Grid.SetColumn(passes, 3);
            header.Children.Add(passes);
        }

        var restStyle = (Style)Application.Current.Resources["ListRowRestStyle"];
        var hoverStyle = (Style)Application.Current.Resources["ListRowHoverStyle"];
        header.Style = restStyle;
        header.CornerRadius = new CornerRadius(4);
        header.PointerEntered += (_, _) => header.Style = hoverStyle;
        header.PointerExited += (_, _) => header.Style = restStyle;
        header.PointerCanceled += (_, _) => header.Style = restStyle;

        if (item.Parameters is { } effect && (effect.Uniforms.Any(IsShown) || effect.PreprocessorDefinitions.Count != 0))
        {
            parts.Chevron = new FontIcon { Glyph = ChevronDown, FontSize = 12 };
            parts.ExpandButton = new Button { Content = parts.Chevron, Width = 32, Height = 32, Style = (Style)Application.Current.Resources["SubtleIconButtonStyle"] };
            AutomationProperties.SetAutomationId(parts.ExpandButton, "ExpandEffect_" + MakeAutomationId(item.Name));
            parts.ExpandButton.Click += (_, _) => SetEffectExpanded(parts, parts.Parameters?.Visibility != Visibility.Visible);
            Grid.SetColumn(parts.ExpandButton, 4);
            header.Children.Add(parts.ExpandButton);
            // The whole header toggles the parameters, except its own buttons and switch.
            header.Tapped += (_, args) =>
            {
                if (IsInsideControl(args.OriginalSource as DependencyObject, header)) return;
                args.Handled = true;
                SetEffectExpanded(parts, parts.Parameters?.Visibility != Visibility.Visible);
            };

            parts.Parameters = new StackPanel { Margin = new Thickness(30, 2, 6, 16), Visibility = Visibility.Collapsed };
            Grid.SetRow(parts.Parameters, 1);
            row.Children.Add(parts.Parameters);
            SetEffectExpanded(parts, _expandedEffects.Contains(item.Name) || MatchesOnlyByParameter(item), remember: false);
        }

        if (item.IsEnabled)
        {
            row.AllowDrop = true;
            row.DragOver += OnEffectDragOver;
            row.Drop += OnEffectDrop;
        }
        return row;
    }

    private static bool IsInsideControl(DependencyObject? source, DependencyObject stop)
    {
        for (var current = source; current != null && !ReferenceEquals(current, stop); current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase or ToggleSwitch) return true;
        return false;
    }

    // "radio" uniforms without items only carry help text (ui_text); show that text, or nothing if it is empty.
    private static bool IsInfoOnly(UniformViewModel uniform) => uniform.UiType == "radio" && uniform.Items.Length == 0;
    private static bool IsShown(UniformViewModel uniform) => !IsInfoOnly(uniform) || uniform.Description.Length != 0;

    private bool UniformMatchesQuery(UniformViewModel uniform) =>
        uniform.Label.Contains(_effectQuery, StringComparison.OrdinalIgnoreCase) || uniform.Name.Contains(_effectQuery, StringComparison.OrdinalIgnoreCase);

    // Searching for a parameter opens the effects that contain it and lists only the matches.
    private bool MatchesOnlyByParameter(EffectListItem item) => _effectQuery.Length != 0 &&
        !item.Name.Contains(_effectQuery, StringComparison.OrdinalIgnoreCase) &&
        !item.Techniques.Any(technique => technique.Name.Contains(_effectQuery, StringComparison.OrdinalIgnoreCase));

    private static ToggleSwitch CreateCompactSwitch(bool isOn)
    {
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        toggle.Resources["ToggleSwitchPreContentMargin"] = 0.0;
        toggle.Resources["ToggleSwitchPostContentMargin"] = 0.0;
        return toggle;
    }

    private FrameworkElement CreatePassesButton(EffectListItem item)
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        foreach (var technique in item.Techniques)
        {
            var entry = new ToggleMenuFlyoutItem { Text = technique.Name, IsChecked = technique.IsEnabled };
            AutomationProperties.SetAutomationId(entry, "Technique_" + MakeAutomationId(technique.Id));
            entry.Click += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetTechniqueStateAsync(technique, entry.IsChecked));
            menu.Items.Add(entry);
        }
        var button = new DropDownButton
        {
            Content = $"{item.EnabledCount}/{item.Techniques.Count}",
            Flyout = menu,
            FontSize = 12,
            Padding = new Thickness(8, 2, 6, 2),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, $"Passes in {item.DisplayName}: {item.EnabledCount} of {item.Techniques.Count} on");
        ToolTipService.SetToolTip(button, "Passes in this effect");
        return button;
    }

    // Rows restore their state with remember: false, so a search can open effects without keeping them open.
    private void SetEffectExpanded(EffectRowParts parts, bool expanded, bool remember = true)
    {
        if (parts.Parameters == null || parts.Item.Parameters == null || parts.Chevron == null || parts.ExpandButton == null) return;
        if (remember && expanded) _expandedEffects.Add(parts.Item.Name);
        else if (remember) _expandedEffects.Remove(parts.Item.Name);
        if (expanded) PopulateEffect(parts.Parameters, parts.Item.Parameters);
        else ReleaseEffect(parts.Parameters);
        parts.Parameters.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        parts.Chevron.Glyph = expanded ? ChevronUp : ChevronDown;
        AutomationProperties.SetName(parts.ExpandButton, (expanded ? "Hide parameters of " : "Show parameters of ") + parts.Item.DisplayName);
        ToolTipService.SetToolTip(parts.ExpandButton, expanded ? "Hide parameters" : "Show parameters");
    }

    private void PopulateEffect(StackPanel host, EffectControlViewModel effect)
    {
        if (_effectEditors.ContainsKey(host)) return;
        var sliders = _fxMappedSliders.Count; var indicators = _fxLogIndicators.Count;
        host.Children.Clear();
        host.Children.Add(BuildEffectPanel(effect));
        _effectEditors[host] = (_fxMappedSliders.Skip(sliders).ToList(), _fxLogIndicators.Skip(indicators).ToList());
    }

    private void ReleaseEffect(StackPanel? host)
    {
        if (host == null) return;
        if (_effectEditors.Remove(host, out var editor)) {
            foreach (var slider in editor.Sliders) _fxMappedSliders.Remove(slider);
            foreach (var indicator in editor.Indicators) _fxLogIndicators.Remove(indicator);
        }
        host.Children.Clear();
    }

    private void OnPanelMenuOpening(object? sender, object args)
    {
        var width = ViewModel.Settings.EffectsPaneWidth;
        PanelWidthNarrow.IsChecked = width < 360;
        PanelWidthMedium.IsChecked = width >= 360 && width < 460;
        PanelWidthWide.IsChecked = width >= 460;
    }

    private void OnPanelWidthClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
            ViewModel.Settings.EffectsPaneWidth = width;
    }

    private void OnExpandActiveClick(object sender, RoutedEventArgs args)
    {
        foreach (var effect in ViewModel.Effects) _expandedEffects.Add(effect.Name);
        FilterEffects();
    }

    private void OnCollapseAllClick(object sender, RoutedEventArgs args)
    {
        _expandedEffects.Clear();
        FilterEffects();
    }

    private void OnShowLogClick(object sender, RoutedEventArgs args) => FlyoutBase.ShowAttachedFlyout(PanelMoreButton);

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
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        });
        pathsPanel.Children.Add(new TextBlock
        {
            Text = "ReShade log: " + (string.IsNullOrWhiteSpace(ViewModel.AddonLogPath) ? "Not available" : ViewModel.AddonLogPath),
            FontFamily = new FontFamily("Consolas"),
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
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
                        Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    });
                }

                diagnosticsPanel.Children.Add(new Border
                {
                    Padding = new Thickness(10),
                    Style = (Style)Application.Current.Resources["SectionBorderStyle"],
                    BorderThickness = new Thickness(1),
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
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });

        if (ViewModel.Addons.Count == 0)
        {
            AddonsPanel.Children.Add(new TextBlock
            {
                Text = "No ReShade add-on registered successfully. Review the load diagnostics and ReShade log above.",
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
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
                    Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
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
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
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

            if (control.IsTreeNode)
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
                        Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
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
                    ? BuildAddonControlSequence(controls, ref index, control.Kind == "combo_begin" ? "combo_end" : "popup_end")
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

            var editor = new ContentControl { Content = BuildAddonControlEditor(control), IsEnabled = control.IsEnabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
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
                    Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
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

    private void QueueAddonControlUpdate(AddonImGuiControlViewModel control, string value) => ViewModel.QueueAddonEdit(control, value);

    private static void ApplyAddonAutomation(FrameworkElement element, AddonImGuiControlViewModel control)
    {
        AutomationProperties.SetAutomationId(element, "AddonControl_" + MakeAutomationId(control.Id));
        AutomationProperties.SetName(element, control.Label);
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

    private FrameworkElement CreateEffectGrip(EffectListItem item)
    {
        var grip = new Button
        {
            Width = 24,
            Height = 32,
            Content = new FontIcon { Glyph = "\uE76F", FontSize = 14, Style = (Style)Application.Current.Resources["TertiaryIconStyle"] },
            Style = (Style)Application.Current.Resources["SubtleIconButtonStyle"],
            Tag = item.Name,
            CanDrag = true
        };
        grip.DragStarting += OnEffectDragStarting;
        AutomationProperties.SetName(grip, "Move " + item.DisplayName);
        AutomationProperties.SetAutomationId(grip, "MoveEffect_" + MakeAutomationId(item.Name));
        ToolTipService.SetToolTip(grip, "Drag to change the render order, or click for move commands");
        var menu = new MenuFlyout();
        foreach (var (text, offset) in new[] { ("Move up", -1), ("Move down", 1), ("Move to top", int.MinValue), ("Move to bottom", int.MaxValue) })
        {
            var entry = new MenuFlyoutItem { Text = text };
            entry.Click += async (_, _) =>
            {
                var index = _activeEffectOrder.FindIndex(name => string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return;
                var target = Math.Clamp(offset switch { int.MinValue => 0, int.MaxValue => _activeEffectOrder.Count - 1, _ => index + offset }, 0, _activeEffectOrder.Count - 1);
                if (target != index) await ReorderActiveEffectAsync(item.Name, _activeEffectOrder[target], target > index);
            };
            menu.Items.Add(entry);
        }
        grip.Flyout = menu;
        return grip;
    }

    private async Task ReorderActiveEffectAsync(string movingEffectName, string targetEffectName, bool insertAfter)
    {
        var order = _activeEffectOrder.ToList();
        var from = order.FindIndex(name => string.Equals(name, movingEffectName, StringComparison.OrdinalIgnoreCase));
        var to = order.FindIndex(name => string.Equals(name, targetEffectName, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || to < 0 || from == to) return;
        order.RemoveAt(from);
        if (from < to) --to;
        if (insertAfter) ++to;
        order.Insert(Math.Clamp(to, 0, order.Count), movingEffectName);
        await RunUiCommandAsync(() => ViewModel.ReorderEffectsAsync(order));
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
        var draggedEffectName = _draggedEffectName;
        _draggedEffectName = null;
        if (sender is not FrameworkElement { Tag: EffectRowParts parts } target ||
            string.IsNullOrEmpty(draggedEffectName) ||
            string.Equals(draggedEffectName, parts.Item.Name, StringComparison.OrdinalIgnoreCase))
            return;

        // Compare against the header so dropping on an expanded effect is not always "after".
        var insertAfter = args.GetPosition(target).Y > Math.Min(target.ActualHeight, 40.0) * 0.5;
        await ReorderActiveEffectAsync(draggedEffectName, parts.Item.Name, insertAfter);
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

    private FrameworkElement CreateAddonNestedSectionExpander(AddonImGuiControlViewModel control, UIElement content, string automationPrefix)
    {
        var expander = new Expander { Header = control.Label, IsExpanded = control.IsOpen, Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ApplyAddonAutomation(expander, control);
        AutomationProperties.SetAutomationId(expander, automationPrefix + MakeAutomationId(control.Id));
        expander.Expanding += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync(control, "true"));
        expander.Collapsed += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetAddonImGuiValueAsync(control, "false"));
        return expander;
    }
    private FrameworkElement BuildEffectPanel(EffectControlViewModel effect)
    {
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        var uniforms = effect.Uniforms.Where(IsShown).ToArray();
        var definitions = effect.PreprocessorDefinitions.ToArray();
        if (_effectQuery.Length != 0 && !effect.Name.Contains(_effectQuery, StringComparison.OrdinalIgnoreCase))
        {
            var matchingUniforms = uniforms.Where(UniformMatchesQuery).ToArray();
            var matchingDefinitions = definitions.Where(definition => definition.Name.Contains(_effectQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matchingUniforms.Length != 0 || matchingDefinitions.Length != 0) (uniforms, definitions) = (matchingUniforms, matchingDefinitions);
        }

        var groups = uniforms.GroupBy(static uniform => uniform.Category).ToArray();
        var showHeaders = groups.Length > 1 || definitions.Length != 0;
        foreach (var group in groups)
        {
            var groupPanel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var uniform in group)
                groupPanel.Children.Add(BuildUniformEditor(uniform));
            // Sections that only hold help text (e.g. Marty's "overview / help") start closed.
            panel.Children.Add(showHeaders ? CreateCategorySection(effect.Name, group.Key, groupPanel, collapsedByDefault: group.All(IsInfoOnly)) : groupPanel);
        }

        if (definitions.Length != 0)
        {
            var definitionsPanel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var definition in definitions)
                definitionsPanel.Children.Add(BuildPreprocessorEditor(definition));
            panel.Children.Add(CreateCategorySection(effect.Name, "Preprocessor definitions", definitionsPanel, collapsedByDefault: true));
        }

        return panel;
    }

    // Flat, collapsible category header; remembers sections the user opened or closed.
    private FrameworkElement CreateCategorySection(string effectName, string title, FrameworkElement content, bool collapsedByDefault)
    {
        var key = effectName + "::" + title;
        var collapsed = collapsedByDefault != _toggledSections.Contains(key);
        var chevron = new FontIcon { Glyph = collapsed ? ChevronRight : ChevronDown, FontSize = 10 };
        var label = new TextBlock
        {
            Text = title.ToUpperInvariant(),
            Style = (Style)Application.Current.Resources["SectionHeaderStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerContent.Children.Add(chevron);
        headerContent.Children.Add(label);
        var header = new Button
        {
            Content = headerContent,
            Style = (Style)Application.Current.Resources["SubtleIconButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4, 6, 4, 6),
            Margin = new Thickness(-4, 0, 0, 0)
        };
        AutomationProperties.SetName(header, title);
        content.Margin = new Thickness(0, 4, 0, 6);
        content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        header.Click += (_, _) =>
        {
            var nowCollapsed = content.Visibility == Visibility.Visible;
            content.Visibility = nowCollapsed ? Visibility.Collapsed : Visibility.Visible;
            chevron.Glyph = nowCollapsed ? ChevronRight : ChevronDown;
            if (nowCollapsed != collapsedByDefault) _toggledSections.Add(key); else _toggledSections.Remove(key);
        };
        var section = new StackPanel();
        section.Children.Add(header);
        section.Children.Add(content);
        return section;
    }

    private FrameworkElement BuildPreprocessorEditor(PreprocessorDefinitionViewModel definition)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = definition.Name, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var row = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textBox = new TextBox { Text = definition.Value, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
        var button = new Button { Content = "Apply", Tag = definition };
        AutomationProperties.SetName(textBox, "Preprocessor definition " + definition.Name);
        AutomationProperties.SetAutomationId(textBox, "Definition_" + MakeAutomationId(definition.EffectName + "_" + definition.Name));
        AutomationProperties.SetName(button, "Apply definition " + definition.Name);
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
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
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
        var indicator = CreateLogIndicator(indicatorCollection);

        var header = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(label);
        Grid.SetColumn(indicator, 1);
        header.Children.Add(indicator);
        return header;
    }

    private TextBlock CreateLogIndicator(ICollection<FrameworkElement> indicatorCollection)
    {
        var indicator = new TextBlock
        {
            Text = "L",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            Visibility = ViewModel.Settings.SliderSymLog ? Visibility.Visible : Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(indicator, "Symmetric logarithmic scale");
        AutomationProperties.SetName(indicator, "Symmetric logarithmic scale enabled");
        indicatorCollection.Add(indicator);
        return indicator;
    }

    // Each parameter is a label row (label, compact editor, reset) plus, for numbers, full-width sliders.
    private FrameworkElement BuildUniformEditor(UniformViewModel uniform)
    {
        var panel = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (IsInfoOnly(uniform))
        {
            panel.Children.Add(new TextBlock { Text = uniform.Description, Style = (Style)Application.Current.Resources["SecondaryTextStyle"], TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            return panel;
        }
        // ui_type "button" is a momentary shader action, not a stored value.
        if (uniform.AddonService.Length == 0 && !uniform.IsReadOnly && uniform.UiType == "button" && uniform.Type == "bool")
        {
            var button = new Button { Content = uniform.Label.Trim(), HorizontalAlignment = HorizontalAlignment.Left };
            if (uniform.Tooltip.Length != 0) ToolTipService.SetToolTip(button, uniform.Tooltip);
            AutomationProperties.SetName(button, uniform.Label.Trim());
            AutomationProperties.SetAutomationId(button, "UniformButton_" + MakeAutomationId(uniform.Id));
            button.Click += async (_, _) => await RunUiCommandAsync(() => ViewModel.PressUniformAsync(uniform));
            panel.Children.Add(button);
            return panel;
        }

        var header = new Grid { ColumnSpacing = 4, MinHeight = 32 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = uniform.Label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var tooltip = uniform.Tooltip.Length != 0 ? uniform.Tooltip : string.Equals(uniform.Label, uniform.Name, StringComparison.Ordinal) ? "" : uniform.Name;
        if (tooltip.Length != 0) ToolTipService.SetToolTip(label, tooltip);
        header.Children.Add(label);
        // Units and the logarithmic-scale marker sit just before the value.
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (uniform.Units.Length != 0) meta.Children.Add(new TextBlock { Text = uniform.Units, Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"], VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(meta, 1);
        header.Children.Add(meta);
        panel.Children.Add(header);
        if (uniform.Description.Length != 0)
            panel.Children.Add(new TextBlock { Text = uniform.Description, Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });

        if (uniform.IsReadOnly)
        {
            var value = new TextBlock
            {
                Text = string.Join(", ", uniform.Values.Select(x => x is double or float ? FormatNumber(ToDouble(x), uniform) : Convert.ToString(x, CultureInfo.InvariantCulture))),
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 160
            };
            ToolTipService.SetToolTip(value, "Managed by the shader; not stored in presets");
            Grid.SetColumn(value, 2);
            header.Children.Add(value);
            return panel;
        }

        var reset = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = 12 }, Width = 28, Height = 28, Style = (Style)Application.Current.Resources["SubtleIconButtonStyle"] };
        AutomationProperties.SetAutomationId(reset, "Reset_" + MakeAutomationId(uniform.Id));
        AutomationProperties.SetName(reset, "Reset " + uniform.Label + " to shader default");
        ToolTipService.SetToolTip(reset, "Reset to shader default (can be undone)");
        reset.Click += async (_, _) => await RunUiCommandAsync(() => ViewModel.ResetUniformAsync(uniform));
        // Add-on service parameters have no shader default to restore.
        if (uniform.AddonService.Length == 0)
        {
            Grid.SetColumn(reset, 3);
            header.Children.Add(reset);
        }

        var hasChoiceEditor = (uniform.UiType == "combo" || uniform.UiType == "list" || uniform.UiType == "radio") && uniform.Items.Length != 0;
        if (hasChoiceEditor)
        {
            var combo = new ComboBox { MinWidth = 120, MaxWidth = 200, Tag = uniform, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(combo, uniform.Label);
            AutomationProperties.SetAutomationId(combo, "UniformChoice_" + MakeAutomationId(uniform.Id));
            foreach (var item in uniform.Items)
                combo.Items.Add(item);
            combo.SelectedIndex = Math.Clamp(ToInt(uniform.Values.FirstOrDefault()), 0, Math.Max(0, combo.Items.Count - 1));
            combo.SelectionChanged += async (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                    await RunUiCommandAsync(() => ViewModel.SetUniformAsync((UniformViewModel)combo.Tag, combo.SelectedIndex));
            };
            Grid.SetColumn(combo, 2);
            header.Children.Add(combo);
            return panel;
        }

        if (uniform.Type == "bool")
        {
            var components = uniform.Values.Length == 0 ? new[] { false } : uniform.Values.Select(ToBool).ToArray();
            async Task CommitBoolAsync() => await RunUiCommandAsync(() => ViewModel.SetUniformAsync(uniform, components.Length == 1 ? components[0] : components.ToArray()));
            if (components.Length == 1)
            {
                var toggle = CreateCompactSwitch(components[0]);
                AutomationProperties.SetAutomationId(toggle, "UniformBool_" + MakeAutomationId(uniform.Id) + "_0");
                AutomationProperties.SetName(toggle, uniform.Label);
                toggle.Toggled += async (_, _) => { components[0] = toggle.IsOn; await CommitBoolAsync(); };
                Grid.SetColumn(toggle, 2);
                header.Children.Add(toggle);
                return panel;
            }

            var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            for (var index = 0; index < components.Length; index++)
            {
                var component = index;
                var checkBox = new CheckBox { Content = ComponentName(uniform, index), IsChecked = components[index], MinWidth = 0 };
                AutomationProperties.SetAutomationId(checkBox, "UniformBool_" + MakeAutomationId(uniform.Id) + "_" + index);
                AutomationProperties.SetName(checkBox, uniform.Label + " " + ComponentName(uniform, index));
                async void Changed(object sender, RoutedEventArgs e) { components[component] = checkBox.IsChecked == true; await CommitBoolAsync(); }
                checkBox.Checked += Changed; checkBox.Unchecked += Changed;
                checks.Children.Add(checkBox);
            }
            panel.Children.Add(checks);
            return panel;
        }

        meta.Children.Add(CreateLogIndicator(_fxLogIndicators));

        // Exact renderer values are kept apart from the rounded display text, so editing one
        // component never re-sends a rounded copy of the others.
        var exact = uniform.Values.Length == 0 ? new[] { 0.0 } : uniform.Values.Select(ToDouble).ToArray();
        var dirty = new bool[exact.Length];
        var textBoxes = new List<TextBox>();
        var sliders = new List<MappedSlider>();
        var editorEpoch = _controlEpoch;
        var textRevision = 0;
        var updatingText = false;
        Action? valuesChanged = null;
        double[] CurrentValues(int changedIndex = -1, double changedValue = 0)
        {
            var values = exact.ToArray();
            for (var i = 0; i < values.Length; ++i)
            {
                if (i == changedIndex) values[i] = changedValue;
                else if (dirty[i] && TryParseNumber(textBoxes[i].Text, out var typed)) values[i] = typed;
            }
            return values;
        }
        void ShowValue(int index, double value)
        {
            updatingText = true;
            textBoxes[index].Text = FormatNumber(value, uniform);
            updatingText = false;
        }
        async Task CommitTextAsync()
        {
            if (!dirty.Any(static flag => flag) || editorEpoch != _controlEpoch) return;
            var revision = textRevision;
            var values = exact.ToArray();
            for (var i = 0; i < values.Length; ++i)
            {
                if (!dirty[i]) continue;
                if (!TryParseNumber(textBoxes[i].Text, out values[i])) { await ViewModel.RejectUniformEditAsync(uniform); return; }
            }
            await ViewModel.SetUniformAsync(uniform, values.Length == 1 ? values[0] : values);
            if (revision != textRevision) return;
            for (var i = 0; i < values.Length; ++i)
            {
                if (!dirty[i]) continue;
                exact[i] = values[i];
                dirty[i] = false;
                sliders[i].SetMappedValue(values[i], false);
                ShowValue(i, values[i]);
            }
            valuesChanged?.Invoke();
        }
        _uniformTextCommits.Add((panel, CommitTextAsync));

        for (var i = 0; i < exact.Length; ++i)
        {
            var componentIndex = i;
            var isSlider = string.Equals(uniform.UiType, "slider", StringComparison.OrdinalIgnoreCase);
            var minimum = uniform.Minimum ?? (isSlider ? 0.0 : exact[i] - Math.Max(1.0, Math.Abs(exact[i]) * 2.0));
            var maximum = uniform.Maximum ?? (isSlider ? 1.0 : exact[i] + Math.Max(1.0, Math.Abs(exact[i]) * 2.0));
            if (Math.Abs(maximum - minimum) < 0.000001)
                maximum = minimum + 1.0;

            var step = uniform.Step ?? (uniform.Type == "float" ? 0.001 : 1.0);
            var slider = new MappedSlider { Tag = uniform, MinWidth = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(slider, exact.Length == 1 ? uniform.Label : uniform.Label + " " + ComponentName(uniform, i));
            AutomationProperties.SetAutomationId(slider, "UniformSlider_" + MakeAutomationId(uniform.Id) + "_" + i);
            slider.ConfigureRange(minimum, maximum, exact[i], step);
            slider.SetInteractionMode(ViewModel.Settings.SliderDragSensitivity, ViewModel.Settings.SliderSymLog);
            slider.DragSensitivityWheelChanged += OnSliderDragSensitivityWheelChanged;
            _fxMappedSliders.Add(slider);
            // Service parameters are private Add-on state outside FX history, so they skip undo gestures.
            if (uniform.AddonService.Length == 0)
            {
                slider.DragStarted += (_, _) => ViewModel.BeginUniformGesture();
                slider.DragCompleted += async (_, _) => await RunUiCommandAsync(ViewModel.FinishUniformGestureAsync);
            }

            var textBox = new TextBox
            {
                Text = FormatNumber(exact[i], uniform),
                Width = 76,
                MinWidth = 0,
                MinHeight = 28,
                Padding = new Thickness(8, 4, 6, 4),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(textBox, exact.Length == 1 ? uniform.Label + " value" : uniform.Label + " " + ComponentName(uniform, i) + " value");
            AutomationProperties.SetAutomationId(textBox, "UniformNumber_" + MakeAutomationId(uniform.Id) + "_" + i);
            textBox.TextChanged += (_, _) => { if (!updatingText) { dirty[componentIndex] = true; textRevision++; } };
            textBoxes.Add(textBox);
            sliders.Add(slider);
            slider.MappedValueChanged += (_, args) =>
            {
                var values = CurrentValues(componentIndex, args.Value);
                exact[componentIndex] = args.Value;
                dirty[componentIndex] = false;
                ShowValue(componentIndex, args.Value);
                QueueUniformUpdate(uniform, values.Length == 1 ? values[0] : values);
                valuesChanged?.Invoke();
            };
            textBox.KeyDown += async (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    await RunUiCommandAsync(CommitTextAsync);
                }
            };
            textBox.LostFocus += async (_, _) => await RunUiCommandAsync(CommitTextAsync);

            if (exact.Length == 1)
            {
                Grid.SetColumn(textBox, 2);
                header.Children.Add(textBox);
                panel.Children.Add(slider);
                continue;
            }

            // Same right-hand columns as the label row, so value boxes line up with scalar ones.
            var row = new Grid { ColumnSpacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            textBox.Margin = new Thickness(4, 0, 0, 0);
            row.Children.Add(new TextBlock
            {
                Text = ComponentName(uniform, i),
                Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"],
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);
            Grid.SetColumn(textBox, 2);
            row.Children.Add(textBox);
            panel.Children.Add(row);
        }

        if (string.Equals(uniform.UiType, "color", StringComparison.OrdinalIgnoreCase) && exact.Length is 3 or 4)
        {
            // The picker works in 0-1; HDR or out-of-gamut values stay editable in the numeric rows.
            static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
            Windows.UI.Color CurrentColor() => Windows.UI.Color.FromArgb(exact.Length == 4 ? Channel(exact[3]) : (byte)255, Channel(exact[0]), Channel(exact[1]), Channel(exact[2]));
            var fill = new SolidColorBrush(CurrentColor());
            var picker = new ColorPicker { Color = CurrentColor(), IsAlphaEnabled = exact.Length == 4, IsMoreButtonVisible = true };
            AutomationProperties.SetName(picker, uniform.Label);
            AutomationProperties.SetAutomationId(picker, "UniformColor_" + MakeAutomationId(uniform.Id));
            var flyout = new Flyout { Content = picker };
            var swatch = new Button
            {
                Content = new Border { Width = 32, Height = 20, CornerRadius = new CornerRadius(4), Background = fill, Style = (Style)Application.Current.Resources["ColorSwatchStyle"] },
                Padding = new Thickness(4),
                Style = (Style)Application.Current.Resources["SubtleIconButtonStyle"],
                Flyout = flyout
            };
            AutomationProperties.SetName(swatch, "Pick colour for " + uniform.Label);
            ToolTipService.SetToolTip(swatch, "Pick a colour");
            valuesChanged = () => fill.Color = CurrentColor();
            var syncingPicker = false;
            picker.ColorChanged += (_, args) =>
            {
                if (syncingPicker) return;
                var color = args.NewColor;
                var channels = new[] { color.R / 255.0, color.G / 255.0, color.B / 255.0, color.A / 255.0 };
                for (var i = 0; i < exact.Length; ++i)
                {
                    exact[i] = channels[i];
                    dirty[i] = false;
                    sliders[i].SetMappedValue(channels[i], false);
                    ShowValue(i, channels[i]);
                }
                fill.Color = CurrentColor();
                QueueUniformUpdate(uniform, exact.ToArray());
            };
            // One picker session is one undo step, like one slider drag.
            flyout.Opened += (_, _) =>
            {
                syncingPicker = true; picker.Color = CurrentColor(); syncingPicker = false;
                if (uniform.AddonService.Length == 0) ViewModel.BeginUniformGesture();
            };
            if (uniform.AddonService.Length == 0) flyout.Closed += async (_, _) => await RunUiCommandAsync(ViewModel.FinishUniformGestureAsync);
            Grid.SetColumn(swatch, 2);
            header.Children.Add(swatch);
        }

        return panel;
    }

    private static string ComponentName(UniformViewModel uniform, int index) =>
        (string.Equals(uniform.UiType, "color", StringComparison.OrdinalIgnoreCase) ? "RGBA" : "XYZW") is var names && index < names.Length
            ? names[index].ToString() : (index + 1).ToString(CultureInfo.InvariantCulture);

    // Floats are shown at single precision so 0.3f reads "0.3" rather than "0.300000012".
    private static string FormatNumber(double value, UniformViewModel uniform) => uniform.Type == "float"
        ? value.ToString("G7", CultureInfo.InvariantCulture)
        : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private void QueueUniformUpdate(UniformViewModel uniform, object value) => ViewModel.QueueUniformEdit(uniform, value);
    private void FlushVisibleEdits()
    {
        FlushAddonOverlayMouseMove();
        _uniformTextCommits.RemoveAll(x => x.Owner.XamlRoot == null);
        foreach (var entry in _uniformTextCommits.ToArray()) _ = RunUiCommandAsync(entry.Commit);
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
            ViewModel.ReportUiError(ex);
            return false;
        }
    }
}
