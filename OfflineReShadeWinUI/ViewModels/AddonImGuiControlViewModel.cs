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
        RawLabel = label;
        // Text and tooltips print "##" literally; widget labels hide everything after it.
        (Label, IdHint) = kind is "text" or "tooltip" ? (StripIcons(label), string.Empty) : SplitImGuiLabel(label);
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
    /// <summary>The label exactly as captured, including ImGui "##id" suffixes and icon glyphs.</summary>
    public string RawLabel { get; }
    /// <summary>The text ImGui shows: the part before "##", without icon-font glyphs. May be empty.</summary>
    public string Label { get; }
    /// <summary>Words from a hidden "##id", e.g. "Temp Tint" for "##TempTint"; empty when there is none.</summary>
    public string IdHint { get; }
    /// <summary>Text drawn just before an unlabeled widget, which add-ons use as its label.</summary>
    public string? Caption { get; private set; }
    /// <summary>A text widget that labels the next control; the view shows it as that control's label instead.</summary>
    public bool IsCaptionOfNext { get; private set; }
    public string DisplayLabel => Caption ?? (Label.Length != 0 ? Label : IdHint);
    public string AccessibleName => DisplayLabel.Length != 0 ? DisplayLabel : Kind.Replace('_', ' ');
    /// <summary>Per-component names for a multi-value widget whose hidden id names each part ("##TempTint" → Temp, Tint).</summary>
    public IReadOnlyList<string> ComponentNames => Components > 1 && IdHint.Split(' ') is { } words && words.Length == Components ? words : Array.Empty<string>();
    public string Value { get; private set; }
    public string Minimum { get; }
    public string Maximum { get; }
    public IReadOnlyList<string> Items { get; }
    public int Components { get; }

    public bool IsEnabled { get; init; } = true;
    public string GroupKey => Addon + " / " + Overlay + " / " + Window;
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
    public bool IsPopupBegin => Kind == "popup_begin" || Kind == "popup_modal_begin" || Kind == "combo_begin";
    public bool IsPopupEnd => Kind == "popup_end" || Kind == "combo_end";
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

    public bool TakesCaption => IsNumeric || IsCombo || IsCheckbox || IsColor || IsTextInput;

    /// <summary>
    /// Add-ons often draw a text line and then an unlabeled widget ("Location X" then DragFloat("##x")).
    /// Pair them so the widget is labeled and the separate text line is not repeated.
    /// </summary>
    public static void ApplyCaptions(IReadOnlyList<AddonImGuiControlViewModel> controls)
    {
        for (var i = 0; i + 1 < controls.Count; ++i)
        {
            var text = controls[i];
            var next = controls[i + 1];
            if (!text.IsText || text.Label.Length == 0 || next.Label.Length != 0 || !next.TakesCaption || text.GroupKey != next.GroupKey)
                continue;
            next.Caption = text.Label;
            text.IsCaptionOfNext = true;
        }
    }

    private static (string Label, string IdHint) SplitImGuiLabel(string label)
    {
        // ImGui shows only the text before "##"; "###" also hides it. Icon fonts use the private-use area.
        var split = label.IndexOf("##", StringComparison.Ordinal);
        var visible = split < 0 ? label : label[..split];
        var id = split < 0 ? string.Empty : label[(split + 2)..].TrimStart('#');
        return (StripIcons(visible), Humanize(StripIcons(id)));
    }

    // Icon fonts (Font Awesome in Marty's add-ons) map glyphs to the private-use area; WinUI fonts show boxes.
    private static string StripIcons(string text) => new string(text.Where(ch => ch is < '' or > '').ToArray()).Trim();

    private static string Humanize(string id)
    {
        if (id.Length == 0 || id.Any(char.IsWhiteSpace) || !id.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            return id.Any(char.IsLetter) ? id : string.Empty;
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in id)
        {
            if (ch == '_' || (char.IsUpper(ch) && current.Length != 0 && !char.IsUpper(current[^1])))
            {
                if (current.Length != 0) words.Add(current.ToString());
                current.Clear();
                if (ch == '_') continue;
            }
            current.Append(ch);
        }
        if (current.Length != 0) words.Add(current.ToString());
        // Bare numeric or single-letter ids ("##0", "##x") say nothing useful.
        if (words.Count == 0 || words.All(word => word.Length < 2 || word.All(char.IsDigit))) return string.Empty;
        var text = string.Join(' ', words);
        return char.ToUpperInvariant(text[0]) + text[1..];
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
