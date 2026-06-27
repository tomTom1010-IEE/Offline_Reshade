using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using OfflineReShade.WinUI.Services;
using OfflineReShade.WinUI.ViewModels;
using WinRT.Interop;

namespace OfflineReShade.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private readonly PreviewHostService _previewHost;
    private readonly Dictionary<string, CancellationTokenSource> _uniformUpdateSources = new();

    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();

        var windowHandle = WindowNative.GetWindowHandle(this);
        var paths = new AppPaths();
        ViewModel = new MainWindowViewModel(paths);
        _previewHost = new PreviewHostService(windowHandle, PreviewSurface);
        ViewModel.Initialize(new SettingsPickerService(() => windowHandle), () => _previewHost.EnsureHandle());
        ViewModel.ControlsChanged += BuildControls;
        Closed += (_, _) =>
        {
            ViewModel.Dispose();
            _previewHost.Dispose();
        };

        BuildControls();
    }

    private void BuildControls()
    {
        ControlsPanel.Children.Clear();

        var effectsEnabled = new CheckBox
        {
            Content = "Effects Enabled",
            IsChecked = ViewModel.EffectsEnabled,
            Margin = new Thickness(0, 0, 0, 8)
        };
        effectsEnabled.Checked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetEffectsEnabledAsync(true));
        effectsEnabled.Unchecked += async (_, _) => await RunUiCommandAsync(() => ViewModel.SetEffectsEnabledAsync(false));
        ControlsPanel.Children.Add(effectsEnabled);

        var techniquePanel = new StackPanel { Spacing = 4 };
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
        ControlsPanel.Children.Add(new Expander { Header = "Techniques", IsExpanded = true, Content = techniquePanel });

        foreach (var effect in ViewModel.Effects)
        {
            ControlsPanel.Children.Add(new Expander
            {
                Header = effect.Name,
                IsExpanded = false,
                Content = BuildEffectPanel(effect),
                Margin = new Thickness(0, 0, 0, 6)
            });
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
    }

    private FrameworkElement BuildEffectPanel(EffectControlViewModel effect)
    {
        var panel = new StackPanel { Spacing = 8 };

        if (effect.PreprocessorDefinitions.Count != 0)
        {
            var definitionsPanel = new StackPanel { Spacing = 8 };
            foreach (var definition in effect.PreprocessorDefinitions)
                definitionsPanel.Children.Add(BuildPreprocessorEditor(definition));

            panel.Children.Add(new Expander
            {
                Header = "Preprocessor Definitions",
                IsExpanded = false,
                Content = definitionsPanel
            });
        }

        foreach (var group in effect.Uniforms.GroupBy(static uniform => uniform.Category))
        {
            var groupPanel = new StackPanel { Spacing = 8 };
            foreach (var uniform in group)
                groupPanel.Children.Add(BuildUniformEditor(uniform));

            panel.Children.Add(new Expander
            {
                Header = group.Key,
                IsExpanded = true,
                Content = groupPanel
            });
        }

        return panel;
    }

    private FrameworkElement BuildPreprocessorEditor(PreprocessorDefinitionViewModel definition)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = definition.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var textBox = new TextBox { Text = definition.Value, MinWidth = 120 };
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
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = uniform.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        if ((uniform.UiType == "combo" || uniform.UiType == "list" || uniform.UiType == "radio") && uniform.Items.Length != 0)
        {
            var combo = new ComboBox { MinWidth = 180, Tag = uniform };
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
        for (var i = 0; i < numericValues.Length; ++i)
        {
            var componentIndex = i;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });

            var minimum = uniform.Minimum ?? numericValues[i] - Math.Max(1.0, Math.Abs(numericValues[i]) * 2.0);
            var maximum = uniform.Maximum ?? numericValues[i] + Math.Max(1.0, Math.Abs(numericValues[i]) * 2.0);
            if (Math.Abs(maximum - minimum) < 0.000001)
                maximum = minimum + 1.0;

            var slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = Math.Clamp(numericValues[i], minimum, maximum),
                StepFrequency = uniform.Step ?? (uniform.Type == "float" ? 0.001 : 1.0),
                Tag = uniform
            };
            var textBox = new TextBox { Text = numericValues[i].ToString("0.######") };
            textBoxes.Add(textBox);
            slider.ValueChanged += (_, _) =>
            {
                textBox.Text = slider.Value.ToString("0.######");
                QueueUniformUpdate(uniform, BuildNumericValue(textBoxes, componentIndex, slider.Value));
            };
            textBox.KeyDown += async (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter && TryBuildNumericValue(textBoxes, out var value))
                    await RunUiCommandAsync(() => ViewModel.SetUniformAsync(uniform, value));
            };
            textBox.LostFocus += async (_, _) =>
            {
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
