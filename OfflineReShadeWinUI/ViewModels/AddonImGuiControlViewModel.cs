using System.Globalization;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class AddonImGuiControlViewModel
{
    public AddonImGuiControlViewModel(
        string id,
        string addon,
        string overlay,
        string window,
        string kind,
        string label,
        string value,
        string minimum,
        string maximum,
        int components)
    {
        Id = id;
        Addon = addon;
        Overlay = overlay;
        Window = window;
        Kind = kind;
        Label = string.IsNullOrWhiteSpace(label) ? kind : label;
        Value = value;
        Minimum = minimum;
        Maximum = maximum;
        Components = components;
    }

    public string Id { get; }
    public string Addon { get; }
    public string Overlay { get; }
    public string Window { get; }
    public string Kind { get; }
    public string Label { get; }
    public string Value { get; }
    public string Minimum { get; }
    public string Maximum { get; }
    public int Components { get; }

    public string GroupKey => string.IsNullOrWhiteSpace(Overlay) ? Addon : Addon + " / " + Overlay;
    public bool IsButton => Kind == "button";
    public bool IsCheckbox => Kind == "checkbox";
    public bool IsCombo => Kind == "combo";
    public bool IsNumeric => Kind.Contains("slider", StringComparison.OrdinalIgnoreCase) ||
        Kind.Contains("drag", StringComparison.OrdinalIgnoreCase) ||
        Kind.Contains("input", StringComparison.OrdinalIgnoreCase) ||
        Kind == "color";

    public bool BoolValue => Value.Equals("true", StringComparison.OrdinalIgnoreCase) || Value == "1";
    public double NumericValue => ParseFirstNumber(Value, 0.0);
    public double NumericMinimum => string.IsNullOrWhiteSpace(Minimum) ? NumericValue - Math.Max(1.0, Math.Abs(NumericValue) * 2.0) : ParseFirstNumber(Minimum, 0.0);
    public double NumericMaximum
    {
        get
        {
            var minimum = NumericMinimum;
            var maximum = string.IsNullOrWhiteSpace(Maximum) ? NumericValue + Math.Max(1.0, Math.Abs(NumericValue) * 2.0) : ParseFirstNumber(Maximum, minimum + 1.0);
            return Math.Abs(maximum - minimum) < 0.000001 ? minimum + 1.0 : maximum;
        }
    }

    private static double ParseFirstNumber(string text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        foreach (var token in text.Split(new[] { ',', ';', ' ', '\t', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return fallback;
    }
}
