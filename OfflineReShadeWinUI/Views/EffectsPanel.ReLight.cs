using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OfflineReShade.WinUI.ViewModels;

namespace OfflineReShade.WinUI.Views;

// ReLight's editor changes one light at a time, and in a game you pick it by clicking its marker in the
// image. The preview here does not forward those clicks, so list the lights and select one through the
// version-verified Marty adapter; the mirrored Light Properties then follow the selection.
public sealed partial class EffectsPanel
{
    private sealed record ReLightLight(int Slot, double X, double Y, double Intensity, double[]? TemperatureTint);

    private ReLightLight[]? _reLightLights;
    private int _reLightSelected = -1;
    private bool _reLightAvailable = true;
    private int _reLightGeneration;
    private ListView? _reLightList;
    private TextBlock? _reLightCount, _reLightStatus;
    private bool _updatingReLightList;
    private DispatcherTimer? _reLightRefreshTimer;

    private static bool IsReLight(AddonImGuiControlViewModel control) => control.Addon == MainWindowViewModel.ReLightAddon;

    private bool IsReLightListAnchor(AddonImGuiControlViewModel control) =>
        _reLightAvailable && IsReLight(control) && control.IsButton && control.DisplayLabel.Equals("Create New Light", StringComparison.OrdinalIgnoreCase);

    // The add-on asks for a click on a light in the game view; the list replaces that instruction.
    private bool IsReplacedReLightHint(AddonImGuiControlViewModel control) =>
        _reLightAvailable && IsReLight(control) && control.IsText && control.DisplayLabel.StartsWith("Select a light on-screen", StringComparison.OrdinalIgnoreCase);

    private FrameworkElement BuildReLightLightList()
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 4) };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Lights", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _reLightCount = new TextBlock { Style = (Style)Application.Current.Resources["SecondaryTextStyle"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_reLightCount, 1);
        header.Children.Add(_reLightCount);
        panel.Children.Add(header);

        _reLightList = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(_reLightList, "ReLightLights");
        AutomationProperties.SetName(_reLightList, "ReLight lights");
        ToolTipService.SetToolTip(_reLightList, "Choose the light that Light Properties edits");
        _reLightList.SelectionChanged += OnReLightSelectionChanged;
        panel.Children.Add(_reLightList);

        _reLightStatus = new TextBlock { Style = (Style)Application.Current.Resources["SecondaryTextStyle"], TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(_reLightStatus, "ReLightLightsStatus");
        AutomationProperties.SetLiveSetting(_reLightStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(_reLightStatus);

        ShowReLightLights();
        ScheduleReLightRefresh(_reLightLights == null ? 0 : 350);
        return panel;
    }

    private void ShowReLightLights()
    {
        if (_reLightList is not { } list) return;
        var lights = _reLightLights ?? Array.Empty<ReLightLight>();
        _updatingReLightList = true;
        try
        {
            if (list.Items.Count != lights.Length)
            {
                list.Items.Clear();
                foreach (var light in lights) list.Items.Add(new ListViewItem());
            }
            for (var i = 0; i < lights.Length; ++i) UpdateReLightItem((ListViewItem)list.Items[i], lights[i]);
            list.SelectedIndex = Array.FindIndex(lights, light => light.Slot == _reLightSelected);
        }
        finally { _updatingReLightList = false; }

        if (_reLightCount != null)
            _reLightCount.Text = _reLightLights == null ? "" : lights.Length == 1 ? "1 light" : lights.Length.ToString(CultureInfo.InvariantCulture) + " lights";
        if (_reLightStatus != null)
        {
            _reLightStatus.Text = _reLightLights == null ? "Reading lights…"
                : lights.Length == 0 ? "No lights yet. Create one above."
                : _reLightSelected < 0 ? "Choose a light to edit it below." : "";
            _reLightStatus.Visibility = _reLightStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static void UpdateReLightItem(ListViewItem item, ReLightLight light)
    {
        var name = "Light " + (light.Slot + 1).ToString(CultureInfo.InvariantCulture);
        // The same numbers the Exposure and Location X/Y sliders show for this light.
        var detail = light.Intensity <= 0 ? "Off"
            : string.Create(CultureInfo.InvariantCulture, $"Exposure {light.Intensity:0.##} · X {light.X:0.00} · Y {light.Y:0.00}");
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new Ellipse
        {
            Width = 14,
            Height = 14,
            Fill = new SolidColorBrush(ReLightColor(light)),
            // Mid grey with alpha reads as an outline on both light and dark backgrounds.
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0x80, 0x80, 0x80)),
            StrokeThickness = 1,
            Opacity = light.Intensity > 0 ? 1 : 0.35,
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        item.Content = row;
        item.Tag = light.Slot;
        AutomationProperties.SetName(item, name + ", " + detail.Replace(" · ", ", "));
        AutomationProperties.SetAutomationId(item, "ReLightLight_" + light.Slot.ToString(CultureInfo.InvariantCulture));
    }

    // An indicator only: temperature runs warm (negative) to cool (positive), tint magenta (negative) to green.
    private static Windows.UI.Color ReLightColor(ReLightLight light)
    {
        if (light.TemperatureTint is not [var temperature, var tint]) return Windows.UI.Color.FromArgb(255, 200, 200, 200);
        double r = 1, g = 1, b = 1;
        if (temperature < 0) { g -= 0.25 * -temperature; b -= 0.6 * -temperature; }
        else { r -= 0.45 * temperature; g -= 0.15 * temperature; }
        if (tint > 0) { r -= 0.3 * tint; b -= 0.3 * tint; }
        else g -= 0.35 * -tint;
        static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        return Windows.UI.Color.FromArgb(255, Channel(r), Channel(g), Channel(b));
    }

    private async void OnReLightSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingReLightList || sender is not ListView { SelectedItem: ListViewItem { Tag: int slot } } || slot == _reLightSelected) return;
        var generation = ++_reLightGeneration; // Reads that started before this choice are stale.
        var previous = _reLightSelected;
        _reLightSelected = slot;
        ShowReLightLights();
        JsonElement schema = default;
        if (await RunUiCommandAsync(async () => schema = await ViewModel.SelectReLightAsync(slot)))
        {
            if (generation == _reLightGeneration) ApplyReLightSchema(schema);
        }
        else if (generation == _reLightGeneration)
        {
            _reLightSelected = previous;
            ShowReLightLights();
        }
    }

    /// <summary>Called after a mirrored control changed; ReLight's light list follows its edits.</summary>
    private void OnAddonControlCommitted(AddonImGuiControlViewModel control, bool structural)
    {
        if (!IsReLight(control)) return;
        // Create and Delete renumber lights; show the add-on's new state promptly.
        if (structural) _ = RunUiCommandAsync(ViewModel.RefreshAddonUiAsync);
        ScheduleReLightRefresh(structural ? 150 : 700);
    }

    private void ScheduleReLightRefresh(int delayMilliseconds)
    {
        if (_reLightRefreshTimer == null)
        {
            _reLightRefreshTimer = new DispatcherTimer();
            _reLightRefreshTimer.Tick += async (_, _) => { _reLightRefreshTimer.Stop(); await RefreshReLightAsync(); };
        }
        _reLightRefreshTimer.Stop();
        _reLightRefreshTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMilliseconds));
        _reLightRefreshTimer.Start();
    }

    private async Task RefreshReLightAsync()
    {
        if (_isClosing || _reLightList == null || !ViewModel.IsPreviewRunning) return;
        // Exports and loads own the session; a read now would be rejected and reported. Try again after.
        if (!ViewModel.CanEditSession) { ScheduleReLightRefresh(1500); return; }
        var generation = ++_reLightGeneration;
        try
        {
            var schema = await ViewModel.ReadAddonServiceAsync(MainWindowViewModel.ReLightAddon);
            if (generation == _reLightGeneration) ApplyReLightSchema(schema);
        }
        catch (NotSupportedException) when (generation == _reLightGeneration) { SetReLightAvailable(false); }
        catch (Exception error) when (generation == _reLightGeneration)
        {
            ViewModel.AppendLog("ReLight lights could not be read: " + error.Message);
        }
    }

    private void ApplyReLightSchema(JsonElement schema)
    {
        var supported = schema.TryGetProperty("privateControls", out var verified) && verified.ValueKind == JsonValueKind.True &&
            schema.TryGetProperty("actions", out var actions) && actions.EnumerateArray().Any(action => action.TryGetProperty("id", out var id) && id.GetString() == "select_light") &&
            schema.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object;
        if (!supported) { SetReLightAvailable(false); return; }
        var lights = new List<ReLightLight>();
        foreach (var light in schema.GetProperty("state").GetProperty("lights").EnumerateArray())
        {
            var position = light.GetProperty("position");
            var tint = light.TryGetProperty("temperatureTint", out var pair) && pair.ValueKind == JsonValueKind.Array ? pair.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
            lights.Add(new ReLightLight(light.GetProperty("slot").GetInt32(), position[0].GetDouble(), position[1].GetDouble(), light.GetProperty("intensity").GetDouble(), tint));
        }
        _reLightLights = lights.ToArray();
        _reLightSelected = schema.GetProperty("state").TryGetProperty("selectedLight", out var selected) ? selected.GetInt32() : -1;
        SetReLightAvailable(true);
        ShowReLightLights();
    }

    private void SetReLightAvailable(bool available)
    {
        if (_reLightAvailable == available) return;
        _reLightAvailable = available;
        // The list and the add-on's own on-screen hint swap places; rebuild the mirrored controls.
        BuildAddonControls();
    }
}
