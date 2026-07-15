using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace OfflineReShade.WinUI.Controls;

public sealed class MappedSliderValueChangedEventArgs : EventArgs
{
    public MappedSliderValueChangedEventArgs(double value)
    {
        Value = value;
    }

    public double Value { get; }
}

public sealed class MappedSliderWheelEventArgs : EventArgs
{
    public MappedSliderWheelEventArgs(int delta)
    {
        Delta = delta;
    }

    public int Delta { get; }
}

public sealed class MappedSlider : UserControl
{
    private const double MinimumRange = 0.000000000001;
    private const double ThumbAllowance = 24.0;

    private readonly Slider _visualSlider;
    private bool _isDragging;
    private uint _capturedPointerId;
    private double _dragStartX;
    private double _dragStartNormalized;
    private double _mappedMinimum;
    private double _mappedMaximum = 1.0;
    private double _mappedStep = 0.001;
    private double _mappedValue;
    private int _dragSensitivity = 1;
    private bool _isLogarithmic;

    public MappedSlider()
    {
        _visualSlider = new Slider
        {
            Minimum = 0.0,
            Maximum = 1.0,
            Value = 0.0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };

        var inputSurface = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent)
        };
        inputSurface.Children.Add(_visualSlider);
        Content = inputSurface;
        IsTabStop = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Center;
    }

    public event EventHandler<MappedSliderValueChangedEventArgs>? MappedValueChanged;
    public event EventHandler<MappedSliderWheelEventArgs>? DragSensitivityWheelChanged;

    public double MappedMinimum => _mappedMinimum;
    public double MappedMaximum => _mappedMaximum;
    public double MappedValue => _mappedValue;
    public bool IsDragging => _isDragging;

    public void ConfigureRange(double minimum, double maximum, double value, double step)
    {
        if (!double.IsFinite(minimum))
            minimum = 0.0;
        if (!double.IsFinite(maximum) || maximum - minimum < MinimumRange)
            maximum = minimum + 1.0;

        _mappedMinimum = minimum;
        _mappedMaximum = maximum;
        _mappedStep = double.IsFinite(step) && step > 0.0 ? step : 0.0;
        SetMappedValue(value, false);
    }

    public void SetInteractionMode(int dragSensitivity, bool isLogarithmic)
    {
        _dragSensitivity = dragSensitivity is 2 or 4 or 8 ? dragSensitivity : 1;
        if (_isLogarithmic == isLogarithmic)
            return;

        _isLogarithmic = isLogarithmic;
        SetMappedValue(_mappedValue, false);
    }

    public void SetMappedValue(double value, bool notify)
    {
        if (!double.IsFinite(value))
            value = _mappedMinimum;

        var mapped = Quantize(Math.Clamp(value, _mappedMinimum, _mappedMaximum));
        var changed = Math.Abs(mapped - _mappedValue) > Math.Max(MinimumRange, Math.Abs(_mappedStep) * 0.000000001);
        _mappedValue = mapped;
        _visualSlider.Value = MappedToNormalized(mapped);

        if (notify && changed)
            MappedValueChanged?.Invoke(this, new MappedSliderValueChangedEventArgs(mapped));
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!IsEnabled || !point.Properties.IsLeftButtonPressed)
        {
            base.OnPointerPressed(e);
            return;
        }

        Focus(FocusState.Pointer);
        _isDragging = true;
        _capturedPointerId = e.Pointer.PointerId;
        _dragStartX = point.Position.X;
        _dragStartNormalized = _visualSlider.Value;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        if (!_isDragging || e.Pointer.PointerId != _capturedPointerId)
        {
            base.OnPointerMoved(e);
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            EndDrag(e);
            return;
        }

        var trackWidth = Math.Max(1.0, ActualWidth - ThumbAllowance);
        var pointerDelta = point.Position.X - _dragStartX;
        var normalized = _dragStartNormalized + pointerDelta / (trackWidth * _dragSensitivity);
        SetMappedValue(NormalizedToMapped(Math.Clamp(normalized, 0.0, 1.0)), true);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (_isDragging && e.Pointer.PointerId == _capturedPointerId)
        {
            EndDrag(e);
            return;
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (!_isDragging && e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            if (delta != 0)
            {
                DragSensitivityWheelChanged?.Invoke(this, new MappedSliderWheelEventArgs(delta));
                e.Handled = true;
                return;
            }
        }

        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        if (_isDragging && e.Pointer.PointerId == _capturedPointerId)
        {
            EndDrag(e);
            return;
        }

        base.OnPointerCanceled(e);
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        _isDragging = false;
        _capturedPointerId = 0;
        base.OnPointerCaptureLost(e);
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        var increment = _mappedStep > 0.0 ? _mappedStep : (_mappedMaximum - _mappedMinimum) / 100.0;
        switch (e.Key)
        {
            case VirtualKey.Left:
            case VirtualKey.Down:
                SetMappedValue(_mappedValue - increment, true);
                e.Handled = true;
                return;
            case VirtualKey.Right:
            case VirtualKey.Up:
                SetMappedValue(_mappedValue + increment, true);
                e.Handled = true;
                return;
            case VirtualKey.Home:
                SetMappedValue(_mappedMinimum, true);
                e.Handled = true;
                return;
            case VirtualKey.End:
                SetMappedValue(_mappedMaximum, true);
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    private void EndDrag(PointerRoutedEventArgs e)
    {
        _isDragging = false;
        _capturedPointerId = 0;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private double Quantize(double value)
    {
        if (_mappedStep <= 0.0)
            return value;

        var steps = Math.Round((value - _mappedMinimum) / _mappedStep, MidpointRounding.AwayFromZero);
        return Math.Clamp(_mappedMinimum + steps * _mappedStep, _mappedMinimum, _mappedMaximum);
    }

    private double MappedToNormalized(double value)
    {
        if (!_isLogarithmic)
            return (value - _mappedMinimum) / (_mappedMaximum - _mappedMinimum);

        var transformedMinimum = SymLog(_mappedMinimum);
        var transformedMaximum = SymLog(_mappedMaximum);
        if (Math.Abs(transformedMaximum - transformedMinimum) < MinimumRange)
            return 0.0;

        return Math.Clamp((SymLog(value) - transformedMinimum) / (transformedMaximum - transformedMinimum), 0.0, 1.0);
    }

    private double NormalizedToMapped(double normalized)
    {
        normalized = Math.Clamp(normalized, 0.0, 1.0);
        if (!_isLogarithmic)
            return _mappedMinimum + normalized * (_mappedMaximum - _mappedMinimum);

        var transformedMinimum = SymLog(_mappedMinimum);
        var transformedMaximum = SymLog(_mappedMaximum);
        return InverseSymLog(transformedMinimum + normalized * (transformedMaximum - transformedMinimum));
    }

    private double SymLog(double value)
    {
        var threshold = LogThreshold;
        return Math.Sign(value) * Math.Log(1.0 + Math.Abs(value) / threshold);
    }

    private double InverseSymLog(double value)
    {
        var threshold = LogThreshold;
        return Math.Sign(value) * threshold * (Math.Exp(Math.Abs(value)) - 1.0);
    }

    private double LogThreshold => Math.Max(_mappedStep > 0.0 ? _mappedStep : 0.0, (_mappedMaximum - _mappedMinimum) * 0.001);
}
