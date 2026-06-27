using System.Collections.ObjectModel;
using System.Text.Json;
using OfflineReShade.WinUI.Mvvm;
using OfflineReShade.WinUI.Services;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths;
    private readonly PrototypeProcessService _prototype;
    private readonly ReShadeRpcClient _rpc = new();
    private readonly SynchronizationContext? _syncContext;
    private SettingsPickerService? _picker;
    private Func<IntPtr>? _previewHandleProvider;
    private bool _isPreviewRunning;
    private bool _isSettingsOpen;
    private bool _effectsEnabled = true;
    private string _statusText = "Ready";
    private string _controlStatusText = "Disconnected";
    private string _previewInfoText = "Full-res ReShade preview";
    private string _logText = string.Empty;

    public MainWindowViewModel(AppPaths paths)
    {
        _paths = paths;
        Settings = new SettingsViewModel(paths);
        _prototype = new PrototypeProcessService(paths);
        _prototype.OutputReceived += AppendLog;
        _prototype.PreviewExited += OnPreviewExited;
        _syncContext = SynchronizationContext.Current;

        StartPreviewCommand = new AsyncRelayCommand(StartPreviewAsync, () => !IsPreviewRunning);
        StopPreviewCommand = new RelayCommand(StopPreview, () => IsPreviewRunning);
        SavePngCommand = new AsyncRelayCommand(SavePngAsync);
        ReShadeShotCommand = new AsyncRelayCommand(ReShadeShotAsync, () => _rpc.IsConnected);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _rpc.IsConnected);
        SavePresetCommand = new AsyncRelayCommand(SavePresetAsync, () => _rpc.IsConnected);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        PickColorCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.ColorPath = path, picker => picker.PickPngAsync()));
        PickDepthCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.DepthPath = path, picker => picker.PickPngAsync()));
        PickEffectDirCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.EffectDir = path, picker => picker.PickFolderAsync()));
        PickPresetCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.PresetPath = path, picker => picker.PickIniAsync()));
        PickOutputCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.OutputPath = path, picker => picker.PickOutputPngAsync()));
    }

    public SettingsViewModel Settings { get; }
    public ObservableCollection<TechniqueViewModel> Techniques { get; } = new();
    public ObservableCollection<EffectControlViewModel> Effects { get; } = new();

    public AsyncRelayCommand StartPreviewCommand { get; }
    public RelayCommand StopPreviewCommand { get; }
    public AsyncRelayCommand SavePngCommand { get; }
    public AsyncRelayCommand ReShadeShotCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand SavePresetCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public AsyncRelayCommand PickColorCommand { get; }
    public AsyncRelayCommand PickDepthCommand { get; }
    public AsyncRelayCommand PickEffectDirCommand { get; }
    public AsyncRelayCommand PickPresetCommand { get; }
    public AsyncRelayCommand PickOutputCommand { get; }

    public event Action? ControlsChanged;

    public bool IsPreviewRunning
    {
        get => _isPreviewRunning;
        private set
        {
            if (SetProperty(ref _isPreviewRunning, value))
            {
                StartPreviewCommand.RaiseCanExecuteChanged();
                StopPreviewCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSettingsOpen { get => _isSettingsOpen; set => SetProperty(ref _isSettingsOpen, value); }
    public bool EffectsEnabled { get => _effectsEnabled; private set => SetProperty(ref _effectsEnabled, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ControlStatusText { get => _controlStatusText; private set => SetProperty(ref _controlStatusText, value); }
    public string PreviewInfoText { get => _previewInfoText; private set => SetProperty(ref _previewInfoText, value); }
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }

    public void Initialize(SettingsPickerService picker, Func<IntPtr> previewHandleProvider)
    {
        _picker = picker;
        _previewHandleProvider = previewHandleProvider;
    }

    public async Task SetEffectsEnabledAsync(bool enabled)
    {
        await _rpc.CallAsync("set_effects_state", new { enabled });
        EffectsEnabled = enabled;
    }

    public async Task SetTechniqueStateAsync(TechniqueViewModel technique, bool enabled)
    {
        technique.IsEnabled = enabled;
        await _rpc.CallAsync("set_technique_state", new { id = technique.Id, enabled });
        await RefreshControlStateAsync();
    }

    public async Task SetUniformAsync(UniformViewModel uniform, object value)
    {
        await _rpc.CallAsync("set_uniform", new { id = uniform.Id, value });
    }

    public async Task SetPreprocessorDefinitionAsync(PreprocessorDefinitionViewModel definition, string value)
    {
        definition.Value = value;
        await _rpc.CallAsync("set_preprocessor_definition", new { effectName = definition.EffectName, name = definition.Name, value });
        await Task.Delay(500);
        await RefreshControlStateAsync();
    }

    private async Task StartPreviewAsync()
    {
        if (_previewHandleProvider == null)
            throw new InvalidOperationException("Preview host is not initialized.");

        try
        {
            StopPreview();
            LogText = string.Empty;
            ControlStatusText = "Connecting";
            var pipeName = "OfflineReShade-" + Guid.NewGuid().ToString("N");
            var previewHwnd = _previewHandleProvider();
            _prototype.StartPreview(BuildPreviewArguments(previewHwnd, pipeName));
            IsPreviewRunning = true;
            StatusText = "Preview running";
            PreviewInfoText = "Full-res ReShade runtime - preview is scaled";

            await _rpc.ConnectAsync(pipeName, CancellationToken.None);
            ControlStatusText = "Connected";
            RaiseControlCommandStates();
            await RefreshControlStateAsync();
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            StopPreview(updateStatus: false);
            ControlStatusText = "Control failed";
            StatusText = "Failed";
        }
    }

    private void StopPreview() => StopPreview(updateStatus: true);

    private void StopPreview(bool updateStatus)
    {
        _rpc.Close();
        _prototype.StopPreview();
        IsPreviewRunning = false;
        if (updateStatus)
            StatusText = "Preview stopped";
        ControlStatusText = "Disconnected";
        RaiseControlCommandStates();
        Techniques.Clear();
        Effects.Clear();
        ControlsChanged?.Invoke();
    }

    private async Task SavePngAsync()
    {
        try
        {
            StatusText = "Saving";
            LogText = string.Empty;
            AppendLog(await _prototype.RunExportAsync(BuildExportArguments()));
            RefreshOutputInfo();
            StatusText = "Saved";
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            StatusText = "Failed";
        }
    }

    private async Task ReShadeShotAsync()
    {
        await _rpc.CallAsync("save_screenshot");
        AppendLog("Screenshot requested.");
    }

    private async Task ReloadAsync()
    {
        await _rpc.CallAsync("reload_effects");
        await Task.Delay(500);
        await RefreshControlStateAsync();
    }

    private async Task SavePresetAsync()
    {
        await _rpc.CallAsync("save_preset");
        AppendLog("Preset saved.");
    }

    private async Task RefreshControlStateAsync()
    {
        JsonElement state = default;
        for (var attempt = 0; attempt < 20; ++attempt)
        {
            state = await _rpc.CallAsync("list_state");
            if ((state.TryGetProperty("techniques", out var techniques) && techniques.GetArrayLength() != 0) ||
                (state.TryGetProperty("uniforms", out var uniforms) && uniforms.GetArrayLength() != 0))
            {
                break;
            }

            await Task.Delay(250);
        }

        var techniquesList = JsonStateParser.ParseTechniques(state);
        var uniformsList = JsonStateParser.ParseUniforms(state);
        var definitionsList = JsonStateParser.ParsePreprocessorDefinitions(state);
        EffectsEnabled = JsonStateParser.ParseEffectsEnabled(state);

        Techniques.Clear();
        foreach (var technique in techniquesList)
            Techniques.Add(technique);

        Effects.Clear();
        foreach (var effect in JsonStateParser.BuildEffects(techniquesList, uniformsList, definitionsList))
            Effects.Add(effect);

        ControlStatusText = "Ready";
        RaiseControlCommandStates();
        ControlsChanged?.Invoke();
    }

    private string BuildExportArguments()
    {
        return BuildCommonArguments()
            .Add("--output", Settings.OutputPath)
            .Add("--width", Settings.RenderWidth)
            .Add("--height", Settings.RenderHeight)
            .ToString();
    }

    private string BuildPreviewArguments(IntPtr previewHwnd, string pipeName)
    {
        return BuildCommonArguments()
            .Add("--parent-hwnd", previewHwnd.ToInt64().ToString())
            .Add("--control-pipe", pipeName)
            .AddSwitch("--interactive")
            .Add("--output", Settings.OutputPath)
            .Add("--width", Settings.RenderWidth)
            .Add("--height", Settings.RenderHeight)
            .ToString();
    }

    private CommandLineBuilder BuildCommonArguments()
    {
        return new CommandLineBuilder()
            .Add("--color", Settings.ColorPath)
            .Add("--depth", Settings.DepthPath)
            .Add("--effect-dir", Settings.EffectDir)
            .Add("--preset", Settings.PresetPath);
    }

    private async Task PickPathAsync(Action<string> setPath, Func<SettingsPickerService, Task<string?>> pick)
    {
        if (_picker == null)
            return;

        var path = await pick(_picker);
        if (!string.IsNullOrWhiteSpace(path))
            setPath(path);
    }

    private void RefreshOutputInfo()
    {
        if (!File.Exists(Settings.OutputPath))
        {
            PreviewInfoText = "Full-res ReShade preview";
            return;
        }

        PreviewInfoText = Path.GetFileName(Settings.OutputPath);
    }

    private void AppendLog(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        void Append()
        {
            LogText = string.IsNullOrEmpty(LogText) ? message : LogText + Environment.NewLine + message;
        }

        if (_syncContext != null)
            _syncContext.Post(_ => Append(), null);
        else
            Append();
    }

    private void OnPreviewExited()
    {
        if (_syncContext != null)
            _syncContext.Post(_ => StopPreview(), null);
        else
            StopPreview();
    }

    private void RaiseControlCommandStates()
    {
        ReShadeShotCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _rpc.Dispose();
        _prototype.Dispose();
    }
}
