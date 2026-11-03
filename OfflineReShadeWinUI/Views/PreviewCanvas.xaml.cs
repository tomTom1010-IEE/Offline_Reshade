using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OfflineReShade.Core;
using OfflineReShade.WinUI.Services;
using OfflineReShade.WinUI.ViewModels;
using Windows.Foundation;
using Windows.System;

namespace OfflineReShade.WinUI.Views;

// Owns only the display surface, view geometry and transient comparison controls.
public sealed partial class PreviewCanvas : UserControl, IDisposable
{
    private readonly PreviewViewport _viewport = new();
    private readonly D3DPreviewBridge _bridge = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private XamlRoot? _root;
    private Point _lastPointer;
    private bool _dragging, _disposed, _changingComparison;
    private int _failures, _cpuWidth, _cpuHeight;
    private DateTimeOffset _retryAt;
    public MainWindowViewModel ViewModel { get; }
    private double Dpi => XamlRoot?.RasterizationScale ?? 1;
    private bool HasPreview => ViewModel.IsPreviewRunning && !ViewModel.ShowEmptyCanvas && ViewModel.RenderWidth > 0 && ViewModel.RenderHeight > 0;
    public PreviewCanvas(MainWindowViewModel vm)
    {
        ViewModel = vm; InitializeComponent();
        ViewModel.PreviewFrameReceived += OnFrame;
        ViewModel.PropertyChanged += OnStateChanged;
        _timer.Tick += (_, _) => Render();
        PreviewSurface.Loaded += OnLoaded;
        PreviewSurface.SizeChanged += (_, _) => UpdateGeometry();
        PreviewSurface.PointerWheelChanged += OnWheel;
        PreviewSurface.PointerPressed += OnPressed;
        PreviewSurface.PointerMoved += OnMoved;
        PreviewSurface.PointerReleased += OnReleased;
        PreviewSurface.PointerCanceled += OnReleased;
        PreviewSurface.PointerCaptureLost += (_, _) => _dragging = false;
        PreviewSurface.DoubleTapped += (_, _) => Fit();
        UpdateTransport();
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_root != null) _root.Changed -= OnRootChanged;
        _root = XamlRoot;
        if (_root != null) _root.Changed += OnRootChanged;
        UpdateGeometry();
    }
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateGeometry();
    private void OnFrame(WriteableBitmap frame)
    {
        if (_disposed) return;
        _cpuWidth = frame.PixelWidth; _cpuHeight = frame.PixelHeight;
        PreviewImage.Source = frame; UpdateGeometry();
    }
    private void OnStateChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_disposed) return;
        if (args.PropertyName == nameof(MainWindowViewModel.IsGpuPreviewActive)) { _failures = 0; _retryAt = default; }
        if (args.PropertyName == nameof(MainWindowViewModel.IsPreviewRunning) && !ViewModel.IsPreviewRunning)
        {
            _bridge.Dispose(); _failures = 0; _retryAt = default;
            PreviewImage.Source = null; _cpuWidth = _cpuHeight = 0;
        }
        if (args.PropertyName is nameof(MainWindowViewModel.IsPreviewRunning) or nameof(MainWindowViewModel.IsGpuPreviewActive) or nameof(MainWindowViewModel.IsSettingsOpen)) UpdateTransport();
        if (args.PropertyName is nameof(MainWindowViewModel.ShowEmptyCanvas) or nameof(MainWindowViewModel.RenderWidth) or nameof(MainWindowViewModel.RenderHeight) or nameof(MainWindowViewModel.SharedPreviewWidth) or nameof(MainWindowViewModel.SharedPreviewHeight) or nameof(MainWindowViewModel.PreviewOriginal)) UpdateGeometry();
    }
    private void UpdateTransport()
    {
        var gpu = ViewModel.IsGpuPreviewActive;
        PreviewSwapChainPanel.Visibility = gpu ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Visibility = gpu ? Visibility.Collapsed : Visibility.Visible;
        if (gpu && ViewModel.IsPreviewRunning && !ViewModel.IsSettingsOpen) _timer.Start();
        else { _timer.Stop(); if (!gpu) _bridge.Dispose(); }
        UpdateGeometry();
    }
    private PreviewLayout Layout() => _viewport.Layout(PreviewSurface.ActualWidth, PreviewSurface.ActualHeight,
        Math.Max(1u, ViewModel.RenderWidth), Math.Max(1u, ViewModel.RenderHeight), Dpi);
    private void UpdateGeometry()
    {
        if (_disposed) return;
        PreviewTools.Visibility = HasPreview ? Visibility.Visible : Visibility.Collapsed;
        if (!HasPreview)
        {
            ZoomStatus.Text = ResolutionStatus.Text = "";
            return;
        }
        var w = PreviewSurface.ActualWidth; var h = PreviewSurface.ActualHeight;
        PreviewSurface.Clip = new RectangleGeometry { Rect = new(0, 0, Math.Max(0, w), Math.Max(0, h)) };
        var layout = Layout();
        PreviewImage.Stretch = Stretch.Fill;
        PreviewImage.HorizontalAlignment = HorizontalAlignment.Left; PreviewImage.VerticalAlignment = VerticalAlignment.Top;
        PreviewImage.Width = layout.Width; PreviewImage.Height = layout.Height;
        PreviewTransform.TranslateX = layout.X; PreviewTransform.TranslateY = layout.Y;
        var scale = _viewport.EffectiveScale(w, h, Math.Max(1u, ViewModel.RenderWidth), Math.Max(1u, ViewModel.RenderHeight), Dpi);
        ZoomStatus.Text = $"{(_viewport.IsFit ? "Fit · " : "")}{scale * 100:0.#}% · {(ViewModel.PreviewOriginal ? "Original input — preview only" : "Processed preview")}";
        var pw = ViewModel.IsGpuPreviewActive ? (int)ViewModel.SharedPreviewWidth : _cpuWidth;
        var ph = ViewModel.IsGpuPreviewActive ? (int)ViewModel.SharedPreviewHeight : _cpuHeight;
        ResolutionStatus.Text = !ViewModel.IsPreviewRunning ? "Open an image to preview" :
            $"Render {ViewModel.RenderWidth} × {ViewModel.RenderHeight} · Preview {pw} × {ph}" +
            (pw > 0 && (pw < ViewModel.RenderWidth || ph < ViewModel.RenderHeight) ? " (downsampled; 100% uses render pixels)" : "");
        Render();
    }
    private void Render()
    {
        if (_disposed || !ViewModel.IsGpuPreviewActive || !ViewModel.IsPreviewRunning || ViewModel.IsSettingsOpen ||
            ViewModel.SharedPreviewHandle == 0 || ViewModel.SharedPreviewWidth == 0 || ViewModel.SharedPreviewHeight == 0 || DateTimeOffset.UtcNow < _retryAt) return;
        try
        {
            var surface = PreviewGeometry.Surface(PreviewSurface.ActualWidth, PreviewSurface.ActualHeight, Dpi);
            _bridge.EnsureCreated(PreviewSwapChainPanel, surface.Width, surface.Height, ViewModel.AdapterLuid);
            var layout = Layout();
            var sx = surface.Width / Math.Max(1, PreviewSurface.ActualWidth); var sy = surface.Height / Math.Max(1, PreviewSurface.ActualHeight);
            _bridge.SetViewTransform(layout.X * sx, layout.Y * sy, layout.Width * sx, layout.Height * sy);
            _bridge.Render(ViewModel.SharedPreviewHandle, ViewModel.SharedPreviewWidth, ViewModel.SharedPreviewHeight);
            _failures = 0;
        }
        catch (Exception error)
        {
            _bridge.Dispose(); _retryAt = DateTimeOffset.UtcNow.AddMilliseconds(400);
            ViewModel.ReportPreviewError($"GPU preview rebuild {++_failures}/3: {error.Message} (0x{error.HResult:X8})");
            if (_failures >= 3) _ = ViewModel.FallBackToCpuPreviewAsync("GPU preview could not recover.");
        }
    }
    public void Fit() { if (!HasPreview) return; _viewport.Fit(); UpdateGeometry(); }
    public void ActualPixels() { if (!HasPreview) return; _viewport.ActualPixels(); UpdateGeometry(); }
    private void Zoom(double factor, Point? point = null)
    {
        if (!HasPreview) return;
        var at = point ?? new Point(PreviewSurface.ActualWidth / 2, PreviewSurface.ActualHeight / 2);
        _viewport.ZoomAt(factor, at.X, at.Y, PreviewSurface.ActualWidth, PreviewSurface.ActualHeight,
            Math.Max(1u, ViewModel.RenderWidth), Math.Max(1u, ViewModel.RenderHeight), Dpi);
        UpdateGeometry();
    }
    public async Task ToggleComparisonAsync()
    {
        if (_changingComparison || !ViewModel.IsPreviewRunning) return;
        _changingComparison = true;
        try { await ViewModel.SetPreviewComparisonAsync(!ViewModel.PreviewOriginal); }
        finally { _changingComparison = false; CompareButton.IsChecked = ViewModel.PreviewOriginal; }
    }
    private async void OnCompare(object sender, RoutedEventArgs args)
    {
        try { await ToggleComparisonAsync(); } catch (Exception error) { ViewModel.ReportUiError(error, "preview_comparison"); }
    }
    private void OnFit(object sender, RoutedEventArgs args) => Fit();
    private void OnActualPixels(object sender, RoutedEventArgs args) => ActualPixels();
    private void OnZoomIn(object sender, RoutedEventArgs args) => Zoom(1.15);
    private void OnZoomOut(object sender, RoutedEventArgs args) => Zoom(1 / 1.15);
    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(PreviewSurface);
        Zoom(point.Properties.MouseWheelDelta > 0 ? 1.15 : 1 / 1.15, point.Position); args.Handled = true;
    }
    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!HasPreview) return;
        var point = args.GetCurrentPoint(PreviewSurface);
        if (point.Properties.IsMiddleButtonPressed) { Fit(); args.Handled = true; return; }
        if (!point.Properties.IsLeftButtonPressed) return;
        CanvasFocusTarget.Focus(FocusState.Pointer);
        _dragging = PreviewSurface.CapturePointer(args.Pointer); _lastPointer = point.Position; args.Handled = true;
    }
    private void OnMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragging) return;
        var point = args.GetCurrentPoint(PreviewSurface);
        if (!point.Properties.IsLeftButtonPressed) { OnReleased(sender, args); return; }
        _viewport.Pan(point.Position.X - _lastPointer.X, point.Position.Y - _lastPointer.Y); _lastPointer = point.Position;
        UpdateGeometry(); args.Handled = true;
    }
    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragging) return;
        _dragging = false; PreviewSurface.ReleasePointerCapture(args.Pointer); args.Handled = true;
    }
    private void OnCanvasKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), CanvasFocusTarget)) return;
        var delta = args.Key switch { VirtualKey.Left => new Point(40, 0), VirtualKey.Right => new Point(-40, 0), VirtualKey.Up => new Point(0, 40), VirtualKey.Down => new Point(0, -40), _ => new Point() };
        if (delta.X == 0 && delta.Y == 0) return;
        _viewport.Pan(delta.X, delta.Y); UpdateGeometry(); args.Handled = true;
    }
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _bridge.Dispose();
        if (_root != null) _root.Changed -= OnRootChanged;
        ViewModel.PreviewFrameReceived -= OnFrame; ViewModel.PropertyChanged -= OnStateChanged;
    }
}
