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
        IReadOnlyList<string> items,
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
        Items = items;
        Components = components;
    }

    public string Id { get; }
    public string Addon { get; }
    public string Overlay { get; }
    public string Window { get; }
    public string Kind { get; }
    public string Label { get; }
    public string Value { get; private set; }
    public string Minimum { get; }
    public string Maximum { get; }
    public IReadOnlyList<string> Items { get; }
    public int Components { get; }

    public string GroupKey => string.IsNullOrWhiteSpace(Overlay) ? Addon : Addon + " / " + Overlay;
    public bool IsButton => Kind == "button";
    public bool IsCheckbox => Kind == "checkbox";
    public bool IsCombo => Kind == "combo" || Kind == "list_box";
    public bool IsText => Kind == "text";
    public bool IsTextInput => Kind == "input_text" || Kind == "input_text_multiline";
    public bool IsMultilineTextInput => Kind == "input_text_multiline";
    public bool IsColor => Kind == "color";
    public bool IsTreeNode => Kind == "tree_node";
    public bool IsCollapsingHeader => Kind == "collapsing_header";
    public bool IsTreeEnd => Kind == "tree_end";
    public bool IsTabBarBegin => Kind == "tab_bar_begin";
    public bool IsTabBarEnd => Kind == "tab_bar_end";
    public bool IsTabItemBegin => Kind == "tab_item_begin";
    public bool IsTabItemEnd => Kind == "tab_item_end";
    public bool IsTooltip => Kind == "tooltip";
    public bool IsPopupBegin => Kind == "popup_begin" || Kind == "popup_modal_begin";
    public bool IsPopupEnd => Kind == "popup_end";
    public bool IsMenuBegin => Kind == "menu_begin";
    public bool IsMenuEnd => Kind == "menu_end";
    public bool IsMenuItem => Kind == "menu_item" || Kind == "selectable";
    public bool IsNativeFallback => Kind == "native_fallback";
    public bool IsOpen => Value.Equals("open", StringComparison.OrdinalIgnoreCase) || BoolValue;
    public bool IsNumeric => Kind == "slider_float" ||
        Kind == "slider_int" ||
        Kind == "drag_float" ||
        Kind == "drag_int" ||
        Kind == "input_float" ||
        Kind == "input_int";

    public bool BoolValue => Value.Equals("true", StringComparison.OrdinalIgnoreCase) || Value == "1";
    public double NumericValue => ParseFirstNumber(Value, 0.0);
    public IReadOnlyList<double> NumericValues => ParseNumbers(Value);
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

    public bool UpdateValue(string value)
    {
        if (string.Equals(Value, value, StringComparison.Ordinal))
            return false;

        Value = value;
        return true;
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

    private static IReadOnlyList<double> ParseNumbers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<double>();

        var result = new List<double>();
        foreach (var token in text.Split(new[] { ',', ';', ' ', '\t', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                result.Add(value);
        }
        return result;
    }
}
