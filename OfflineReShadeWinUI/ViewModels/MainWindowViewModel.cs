using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;
using OfflineReShade.WinUI.Mvvm;
using OfflineReShade.WinUI.Services;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths;
    private readonly PrototypeProcessService _prototype;
    private readonly UserSettingsStore _settingsStore;
    private readonly ReShadeRpcClient _rpc = new();
    private readonly PreviewFrameClient _previewFrames = new();
    private readonly SynchronizationContext? _syncContext;
    private CancellationTokenSource? _fpsCancellation;
    private CancellationTokenSource? _settingsSaveCancellation;
    private SettingsPickerService? _picker;
    private Func<IntPtr>? _previewHostHandleProvider;
    private bool _isPreviewRunning;
    private bool _isSettingsOpen;
    private bool _isBatchApplying;
    private bool _effectsEnabled = true;
    private string _statusText = "Ready";
    private string _controlStatusText = "Disconnected";
    private string _previewInfoText = "Full-res ReShade preview";
    private string _fpsText = string.Empty;
    private string _logText = string.Empty;
    private string _inputMode = "RealTime";
    private bool _isGalleryPanelOpen = true;
    private GalleryItemViewModel? _selectedGalleryItem;
    private ulong _sharedPreviewHandle;
    private uint _sharedPreviewWidth;
    private uint _sharedPreviewHeight;
    private bool _isRestoringSettings;
    private bool _isDisposed;
    private bool _hasPersistedDepthProfile;
    private bool _isApplyingProfilePaths;
    private string _currentDepthProfile = "kks";
    private PersistedProfilePaths _kksPaths = new();
    private PersistedProfilePaths _kkPaths = new();

    public MainWindowViewModel(AppPaths paths)
    {
        _paths = paths;
        _settingsStore = new UserSettingsStore(paths);
        Settings = new SettingsViewModel(paths);
        RestorePersistedSettings();
        _currentDepthProfile = NormalizeDepthProfile(Settings.DepthProfile);
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        NormalizeDepthSettings();
        InferDepthProfileIfNeeded(saveIfInferred: true);
        InitializeProfilePathState();
        _prototype = new PrototypeProcessService(paths);
        _prototype.OutputReceived += OnPrototypeOutputReceived;
        _prototype.PreviewExited += OnPreviewExited;
        _previewFrames.FrameReceived += frame => PreviewFrameReceived?.Invoke(frame);
        _previewFrames.ErrorReceived += AppendLog;
        _syncContext = SynchronizationContext.Current;

        StartPreviewCommand = new AsyncRelayCommand(StartPreviewAsync, () => !IsPreviewRunning);
        StopPreviewCommand = new RelayCommand(StopPreview, () => IsPreviewRunning);
        SavePngCommand = new AsyncRelayCommand(SavePngAsync);
        ReShadeShotCommand = new AsyncRelayCommand(ReShadeShotAsync, () => _rpc.IsConnected);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _rpc.IsConnected);
        SavePresetCommand = new AsyncRelayCommand(SavePresetAsync, () => _rpc.IsConnected);
        ToggleInputModeCommand = new AsyncRelayCommand(ToggleInputModeAsync);
        ToggleGalleryPanelCommand = new RelayCommand(() => IsGalleryPanelOpen = !IsGalleryPanelOpen);
        RefreshGalleryCommand = new RelayCommand(RefreshGalleryItems);
        BatchApplyCommand = new AsyncRelayCommand(BatchApplyGalleryAsync, () => IsGalleryMode && _rpc.IsConnected && !IsBatchApplying);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        PickColorCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.ColorPath = path, picker => picker.PickPngAsync()));
        PickDepthCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.DepthPath = path, picker => picker.PickDepthAsync()));
        PickEffectDirCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.EffectDir = path, picker => picker.PickFolderAsync()));
        PickPresetCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.PresetPath = path, picker => picker.PickIniAsync()));
        PickOutputCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.OutputPath = path, picker => picker.PickOutputPngAsync()));
        PickGalleryInputFolderCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.GalleryInputFolder = path, picker => picker.PickFolderAsync()));
        PickGalleryOutputFolderCommand = new AsyncRelayCommand(async () => await PickPathAsync(path => Settings.GalleryOutputFolder = path, picker => picker.PickFolderAsync()));

        if (IsGalleryMode)
            RefreshGalleryItems();
    }

    public SettingsViewModel Settings { get; }
    public ObservableCollection<TechniqueViewModel> Techniques { get; } = new();
    public ObservableCollection<EffectControlViewModel> Effects { get; } = new();
    public ObservableCollection<GalleryItemViewModel> GalleryItems { get; } = new();

    public AsyncRelayCommand StartPreviewCommand { get; }
    public RelayCommand StopPreviewCommand { get; }
    public AsyncRelayCommand SavePngCommand { get; }
    public AsyncRelayCommand ReShadeShotCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand SavePresetCommand { get; }
    public AsyncRelayCommand ToggleInputModeCommand { get; }
    public RelayCommand ToggleGalleryPanelCommand { get; }
    public RelayCommand RefreshGalleryCommand { get; }
    public AsyncRelayCommand BatchApplyCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public AsyncRelayCommand PickColorCommand { get; }
    public AsyncRelayCommand PickDepthCommand { get; }
    public AsyncRelayCommand PickEffectDirCommand { get; }
    public AsyncRelayCommand PickPresetCommand { get; }
    public AsyncRelayCommand PickOutputCommand { get; }
    public AsyncRelayCommand PickGalleryInputFolderCommand { get; }
    public AsyncRelayCommand PickGalleryOutputFolderCommand { get; }

    public event Action? ControlsChanged;
    public event Action<WriteableBitmap>? PreviewFrameReceived;

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
    public string FpsText { get => _fpsText; private set => SetProperty(ref _fpsText, value); }
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }
    public string InputMode
    {
        get => _inputMode;
        private set
        {
            if (SetProperty(ref _inputMode, value))
            {
                OnPropertyChanged(nameof(IsGalleryMode));
                OnPropertyChanged(nameof(InputModeButtonText));
                BatchApplyCommand.RaiseCanExecuteChanged();
                ScheduleSettingsSave();
            }
        }
    }

    public bool IsGalleryMode => string.Equals(InputMode, "Gallery", StringComparison.OrdinalIgnoreCase);
    public string InputModeButtonText => IsGalleryMode ? "Mode: Gallery" : "Mode: Real Time";
    public bool IsBatchApplying
    {
        get => _isBatchApplying;
        private set
        {
            if (SetProperty(ref _isBatchApplying, value))
                BatchApplyCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsGalleryPanelOpen { get => _isGalleryPanelOpen; set => SetProperty(ref _isGalleryPanelOpen, value); }
    public GalleryItemViewModel? SelectedGalleryItem { get => _selectedGalleryItem; private set => SetProperty(ref _selectedGalleryItem, value); }
    public ulong SharedPreviewHandle { get => _sharedPreviewHandle; private set => SetProperty(ref _sharedPreviewHandle, value); }
    public uint SharedPreviewWidth { get => _sharedPreviewWidth; private set => SetProperty(ref _sharedPreviewWidth, value); }
    public uint SharedPreviewHeight { get => _sharedPreviewHeight; private set => SetProperty(ref _sharedPreviewHeight, value); }

    public void Initialize(SettingsPickerService picker, Func<IntPtr>? previewHostHandleProvider = null)
    {
        _picker = picker;
        _previewHostHandleProvider = previewHostHandleProvider;
    }

    private void RestorePersistedSettings()
    {
        var settings = _settingsStore.Load();
        if (settings == null)
            return;

        _isRestoringSettings = true;
        try
        {
            ApplyIfNotNull(settings.ColorPath, value => Settings.ColorPath = value);
            ApplyIfNotNull(settings.DepthPath, value => Settings.DepthPath = value);
            ApplyIfNotNull(settings.DepthFormat, value => Settings.DepthFormat = value);
            if (!string.IsNullOrWhiteSpace(settings.DepthProfile))
            {
                Settings.DepthProfile = settings.DepthProfile;
                _hasPersistedDepthProfile = true;
            }
            ApplyIfNotNull(settings.DepthDownsample, value => Settings.DepthDownsample = value);
            ApplyIfNotNull(settings.EffectDir, value => Settings.EffectDir = value);
            ApplyIfNotNull(settings.PresetPath, value => Settings.PresetPath = value);
            ApplyIfNotNull(settings.OutputPath, value => Settings.OutputPath = value);
            ApplyIfNotNull(settings.RenderWidth, value => Settings.RenderWidth = value);
            ApplyIfNotNull(settings.RenderHeight, value => Settings.RenderHeight = value);
            ApplyIfNotNull(settings.PreviewTransport, value => Settings.PreviewTransport = value);
            ApplyIfNotNull(settings.GalleryInputFolder, value => Settings.GalleryInputFolder = value);
            ApplyIfNotNull(settings.GalleryOutputFolder, value => Settings.GalleryOutputFolder = value);
            ApplyIfNotNull(settings.GalleryBatchFrameDelay, value => Settings.GalleryBatchFrameDelay = value);
            if (settings.ShowFps.HasValue)
                Settings.ShowFps = settings.ShowFps.Value;
            _kksPaths = settings.KksPaths ?? new PersistedProfilePaths();
            _kkPaths = settings.KkPaths ?? new PersistedProfilePaths();
            if (string.Equals(settings.InputMode, "Gallery", StringComparison.OrdinalIgnoreCase))
                _inputMode = "Gallery";
            else if (string.Equals(settings.InputMode, "RealTime", StringComparison.OrdinalIgnoreCase))
                _inputMode = "RealTime";
        }
        catch (Exception ex)
        {
            AppendLog("Failed to load UI settings: " + ex.Message);
        }
        finally
        {
            _isRestoringSettings = false;
        }
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

    public async Task SelectGalleryItemAsync(GalleryItemViewModel? item)
    {
        if (item == null || !item.IsValid)
            return;

        SelectedGalleryItem = item;
        PreviewInfoText = item.BaseName;
        if (!IsGalleryMode || !_rpc.IsConnected)
            return;

        var result = await _rpc.CallAsync("set_input_paths", new
        {
            colorPath = item.ColorPath,
            depthPath = item.DepthPath,
            depthFormat = item.DepthFormat,
            depthProfile = EffectiveDepthProfile(item.DepthFormat),
            depthDownsample = Settings.DepthDownsample,
            outputPath = item.OutputPath
        });
        ApplyInputSwitchResult(result);
        AppendLog("Gallery item loaded: " + item.BaseName);
    }

    public async Task ToggleInputModeAsync()
    {
        var switchToGallery = !IsGalleryMode;

        if (!IsPreviewRunning)
        {
            InputMode = switchToGallery ? "Gallery" : "RealTime";
            if (IsGalleryMode)
                RefreshGalleryItems();
            return;
        }

        if (!_rpc.IsConnected)
        {
            AppendLog("Cannot hot-switch input mode before the control pipe is connected.");
            return;
        }

        try
        {
            if (switchToGallery)
            {
                RefreshGalleryItems();
                var item = SelectedGalleryItem?.IsValid == true ? SelectedGalleryItem : GalleryItems.FirstOrDefault(galleryItem => galleryItem.IsValid);
                if (item == null)
                {
                    AppendLog("Gallery mode needs a valid *-Color.png + *-Depth.rfloat/png pair, or <frame>.png + <frame>.depth.rfloat pair.");
                    return;
                }

                InputMode = "Gallery";
                SelectedGalleryItem = item;
                await _rpc.CallAsync("set_input_watch_enabled", new { enabled = false });
                await SelectGalleryItemAsync(item);
            }
            else
            {
                var result = await _rpc.CallAsync("set_input_paths", new
                {
                    colorPath = Settings.ColorPath,
                    depthPath = Settings.DepthPath,
                    depthFormat = Settings.DepthFormat,
                    depthProfile = EffectiveDepthProfile(Settings.DepthFormat),
                    depthDownsample = Settings.DepthDownsample,
                    outputPath = Settings.OutputPath
                });
                ApplyInputSwitchResult(result);
                await _rpc.CallAsync("set_input_watch_enabled", new { enabled = true });
                InputMode = "RealTime";
                PreviewInfoText = "Full-res ReShade runtime - preview is scaled";
                AppendLog("Real Time input loaded.");
            }
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
        }
    }

    private async Task StartPreviewAsync()
    {
        try
        {
            StopPreview();
            if (IsGalleryMode)
                EnsureGallerySelection();
            LogText = string.Empty;
            ControlStatusText = "Connecting";
            var pipeName = "OfflineReShade-" + Guid.NewGuid().ToString("N");
            var previewPipeName = pipeName + "-preview";
            if (UseCpuPreview)
                _previewFrames.Start(previewPipeName);
            _prototype.StartPreview(BuildPreviewArguments(pipeName, previewPipeName));
            IsPreviewRunning = true;
            StatusText = "Preview running";
            PreviewInfoText = IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.BaseName : "Full-res ReShade runtime - preview is scaled";

            await _rpc.ConnectAsync(pipeName, CancellationToken.None);
            ControlStatusText = "Connected";
            RaiseControlCommandStates();
            StartFpsPolling();
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
        StopFpsPolling();
        _rpc.Close();
        _previewFrames.Stop();
        _prototype.StopPreview();
        IsPreviewRunning = false;
        if (updateStatus)
            StatusText = "Preview stopped";
        ControlStatusText = "Disconnected";
        FpsText = string.Empty;
        SharedPreviewHandle = 0;
        SharedPreviewWidth = 0;
        SharedPreviewHeight = 0;
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
            var outputDir = Path.GetDirectoryName(ActiveOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);
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
        if (IsGalleryMode && SelectedGalleryItem != null && _rpc.IsConnected)
            await SelectGalleryItemAsync(SelectedGalleryItem);
        await _rpc.CallAsync("save_screenshot");
        AppendLog("Screenshot requested.");
    }

    private async Task BatchApplyGalleryAsync()
    {
        if (!IsGalleryMode)
        {
            AppendLog("Batch apply is only available in Gallery mode.");
            return;
        }

        if (!_rpc.IsConnected)
        {
            AppendLog("Start preview before batch apply.");
            return;
        }

        RefreshGalleryItems();
        var items = GalleryItems.Where(static item => item.IsValid).ToArray();
        if (items.Length == 0)
        {
            AppendLog("No valid gallery items to process.");
            return;
        }

        IsBatchApplying = true;
        try
        {
            var originalItem = SelectedGalleryItem;
            var frameDelay = ParseNonNegativeInt(Settings.GalleryBatchFrameDelay, 5);
            var delay = await BuildFrameDelayAsync(frameDelay);

            AppendLog($"Batch apply started: {items.Length} item(s), frame delay {frameDelay}.");
            for (var i = 0; i < items.Length; ++i)
            {
                var item = items[i];
                StatusText = $"Batch {i + 1}/{items.Length}";
                var outputDir = Path.GetDirectoryName(item.OutputPath);
                if (!string.IsNullOrWhiteSpace(outputDir))
                    Directory.CreateDirectory(outputDir);

                await SelectGalleryItemAsync(item);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay);

                await _rpc.CallAsync("save_screenshot");
                AppendLog($"Batch saved: {item.OutputPath}");
            }

            if (originalItem?.IsValid == true)
                await SelectGalleryItemAsync(originalItem);

            StatusText = "Batch complete";
            AppendLog("Batch apply complete.");
        }
        catch (Exception ex)
        {
            StatusText = "Batch failed";
            AppendLog(ex.Message);
        }
        finally
        {
            IsBatchApplying = false;
        }
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

    private void StartFpsPolling()
    {
        StopFpsPolling();
        _fpsCancellation = new CancellationTokenSource();
        _ = PollFpsAsync(_fpsCancellation.Token);
    }

    private void StopFpsPolling()
    {
        var cancellation = _fpsCancellation;
        _fpsCancellation = null;
        if (cancellation == null)
            return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task PollFpsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!Settings.ShowFps)
                {
                    FpsText = string.Empty;
                }
                else if (_rpc.IsConnected)
                {
                    var info = await _rpc.CallAsync("get_runtime_info", cancellationToken: cancellationToken);
                    var renderFps = info.TryGetProperty("renderFps", out var render) ? render.GetDouble() : 0.0;
                    var previewFps = info.TryGetProperty("previewFps", out var preview) ? preview.GetDouble() : 0.0;
                    SharedPreviewHandle = info.TryGetProperty("sharedPreviewHandle", out var handle) ? handle.GetUInt64() : 0;
                    SharedPreviewWidth = info.TryGetProperty("sharedPreviewWidth", out var previewWidth) ? previewWidth.GetUInt32() : 0;
                    SharedPreviewHeight = info.TryGetProperty("sharedPreviewHeight", out var previewHeight) ? previewHeight.GetUInt32() : 0;
                    FpsText = string.Create(CultureInfo.InvariantCulture, $"Runtime {renderFps:0.0} FPS | Preview {previewFps:0.0} FPS");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                FpsText = "FPS unavailable";
                AppendLog(ex.Message);
            }

            try
            {
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ScheduleSettingsSave();

        if (e.PropertyName == nameof(SettingsViewModel.DepthProfile))
        {
            _hasPersistedDepthProfile = true;
            var nextProfile = NormalizeDepthProfile(Settings.DepthProfile);
            if (!string.Equals(nextProfile, _currentDepthProfile, StringComparison.OrdinalIgnoreCase))
            {
                StoreCurrentProfilePaths(_currentDepthProfile);
                _currentDepthProfile = nextProfile;
                ApplyProfilePaths(nextProfile);
            }

            if (string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Settings.DepthFormat, "raw", StringComparison.OrdinalIgnoreCase))
            {
                Settings.DepthFormat = "raw";
                Settings.DepthPath = Path.Combine(Path.GetDirectoryName(Settings.DepthPath) ?? string.Empty, "depthoutput.rfloat");
                AppendLog("KK depth profile uses raw .rfloat depth.");
            }
        }

        if (e.PropertyName == nameof(SettingsViewModel.DepthFormat) &&
            string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Settings.DepthFormat, "raw", StringComparison.OrdinalIgnoreCase))
        {
            Settings.DepthFormat = "raw";
            AppendLog("KK depth profile only supports raw .rfloat depth.");
        }

        if (!_hasPersistedDepthProfile &&
            (e.PropertyName == nameof(SettingsViewModel.ColorPath) ||
             e.PropertyName == nameof(SettingsViewModel.DepthPath)))
        {
            InferDepthProfileIfNeeded(saveIfInferred: true);
        }

        if (e.PropertyName == nameof(SettingsViewModel.ShowFps) && !Settings.ShowFps)
            FpsText = string.Empty;
        if (e.PropertyName == nameof(SettingsViewModel.GalleryInputFolder) ||
            e.PropertyName == nameof(SettingsViewModel.GalleryOutputFolder))
        {
            RefreshGalleryItems();
        }

        if (!_isRestoringSettings && !_isApplyingProfilePaths && IsProfilePathProperty(e.PropertyName))
            StoreCurrentProfilePaths(_currentDepthProfile);
    }

    private void ScheduleSettingsSave()
    {
        if (_isRestoringSettings || _isDisposed)
            return;

        var oldCancellation = _settingsSaveCancellation;
        _settingsSaveCancellation = new CancellationTokenSource();
        oldCancellation?.Cancel();
        oldCancellation?.Dispose();

        var token = _settingsSaveCancellation.Token;
        _ = SaveSettingsAfterDelayAsync(token);
    }

    private async Task SaveSettingsAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken);
            await SaveSettingsNowAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog("Failed to save UI settings: " + ex.Message);
        }
    }

    private Task SaveSettingsNowAsync(CancellationToken cancellationToken = default)
    {
        return _settingsStore.SaveAsync(CreatePersistedSettings(), cancellationToken);
    }

    private void SaveSettingsNow()
    {
        _settingsStore.Save(CreatePersistedSettings());
    }

    private PersistedUiSettings CreatePersistedSettings()
    {
        StoreCurrentProfilePaths(_currentDepthProfile);

        return new PersistedUiSettings
        {
            ColorPath = Settings.ColorPath,
            DepthPath = Settings.DepthPath,
            DepthFormat = Settings.DepthFormat,
            DepthProfile = Settings.DepthProfile,
            DepthDownsample = Settings.DepthDownsample,
            EffectDir = Settings.EffectDir,
            PresetPath = Settings.PresetPath,
            OutputPath = Settings.OutputPath,
            RenderWidth = Settings.RenderWidth,
            RenderHeight = Settings.RenderHeight,
            PreviewTransport = Settings.PreviewTransport,
            GalleryInputFolder = Settings.GalleryInputFolder,
            GalleryOutputFolder = Settings.GalleryOutputFolder,
            GalleryBatchFrameDelay = Settings.GalleryBatchFrameDelay,
            ShowFps = Settings.ShowFps,
            InputMode = InputMode,
            KksPaths = _kksPaths,
            KkPaths = _kkPaths
        };
    }

    private string BuildExportArguments()
    {
        EnsureGallerySelectionIfNeeded();
        return BuildCommonArguments()
            .Add("--output", ActiveOutputPath)
            .Add("--width", Settings.RenderWidth)
            .Add("--height", Settings.RenderHeight)
            .ToString();
    }

    private string BuildPreviewArguments(string pipeName, string previewPipeName)
    {
        EnsureGallerySelectionIfNeeded();
        var arguments = BuildCommonArguments()
            .Add("--control-pipe", pipeName)
            .AddSwitch("--interactive")
            .Add("--output", ActiveOutputPath)
            .Add("--width", Settings.RenderWidth)
            .Add("--height", Settings.RenderHeight);

        if (IsGalleryMode)
            arguments.AddSwitch("--disable-input-watch");

        if (UseCpuPreview)
        {
            arguments.Add("--preview-pipe", previewPipeName);
        }
        else
        {
            arguments.AddSwitch("--preview-shared");
        }

        return arguments.ToString();
    }

    private CommandLineBuilder BuildCommonArguments()
    {
        return new CommandLineBuilder()
            .Add("--color", ActiveColorPath)
            .Add("--depth", ActiveDepthPath)
            .Add("--depth-format", ActiveDepthFormat)
            .Add("--depth-profile", ActiveDepthProfile)
            .Add("--depth-downsample", Settings.DepthDownsample)
            .Add("--effect-dir", Settings.EffectDir)
            .Add("--preset", Settings.PresetPath);
    }

    private bool UseCpuPreview => string.Equals(Settings.PreviewTransport, "CPU", StringComparison.OrdinalIgnoreCase);
    private string ActiveColorPath => IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.ColorPath : Settings.ColorPath;
    private string ActiveDepthPath => IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.DepthPath : Settings.DepthPath;
    private string ActiveDepthFormat => string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase)
        ? "raw"
        : IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.DepthFormat : Settings.DepthFormat;
    private string ActiveDepthProfile => EffectiveDepthProfile(ActiveDepthFormat);
    private string ActiveOutputPath => IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.OutputPath : Settings.OutputPath;

    private string EffectiveDepthProfile(string depthFormat)
    {
        if (string.Equals(depthFormat, "rgba", StringComparison.OrdinalIgnoreCase))
            return "kks";

        return string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase) ? "kk" : "kks";
    }

    private void NormalizeDepthSettings()
    {
        if (!string.Equals(Settings.DepthDownsample, "box", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Settings.DepthDownsample, "max2x2", StringComparison.OrdinalIgnoreCase))
        {
            Settings.DepthDownsample = "max2x2";
        }

        if (!string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Settings.DepthProfile, "kks", StringComparison.OrdinalIgnoreCase))
        {
            Settings.DepthProfile = "kks";
        }

        if (string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase))
            Settings.DepthFormat = "raw";
    }

    private void InitializeProfilePathState()
    {
        _currentDepthProfile = NormalizeDepthProfile(Settings.DepthProfile);
        if (HasAnyProfilePath(GetProfilePaths(_currentDepthProfile)))
            ApplyProfilePaths(_currentDepthProfile);
        else
            StoreCurrentProfilePaths(_currentDepthProfile);
    }

    private void StoreCurrentProfilePaths(string profile)
    {
        var paths = new PersistedProfilePaths
        {
            ColorPath = Settings.ColorPath,
            DepthPath = Settings.DepthPath,
            DepthFormat = Settings.DepthFormat,
            OutputPath = Settings.OutputPath,
            GalleryInputFolder = Settings.GalleryInputFolder,
            GalleryOutputFolder = Settings.GalleryOutputFolder
        };

        if (string.Equals(NormalizeDepthProfile(profile), "kk", StringComparison.OrdinalIgnoreCase))
            _kkPaths = paths;
        else
            _kksPaths = paths;
    }

    private PersistedProfilePaths GetProfilePaths(string profile)
    {
        return string.Equals(NormalizeDepthProfile(profile), "kk", StringComparison.OrdinalIgnoreCase) ? _kkPaths : _kksPaths;
    }

    private void ApplyProfilePaths(string profile)
    {
        var normalized = NormalizeDepthProfile(profile);
        var saved = GetProfilePaths(normalized);
        var defaults = CreateDefaultProfilePaths(normalized);

        _isApplyingProfilePaths = true;
        try
        {
            Settings.ColorPath = FirstUsablePath(saved.ColorPath, defaults.ColorPath);
            Settings.DepthPath = FirstUsablePath(saved.DepthPath, defaults.DepthPath);
            Settings.OutputPath = FirstUsablePath(saved.OutputPath, defaults.OutputPath);
            Settings.GalleryInputFolder = saved.GalleryInputFolder ?? string.Empty;
            Settings.GalleryOutputFolder = saved.GalleryOutputFolder ?? string.Empty;

            if (string.Equals(normalized, "kk", StringComparison.OrdinalIgnoreCase))
                Settings.DepthFormat = "raw";
            else
                Settings.DepthFormat = NormalizeDepthFormat(saved.DepthFormat ?? defaults.DepthFormat);
        }
        finally
        {
            _isApplyingProfilePaths = false;
        }

        StoreCurrentProfilePaths(normalized);
    }

    private PersistedProfilePaths CreateDefaultProfilePaths(string profile)
    {
        if (string.Equals(NormalizeDepthProfile(profile), "kk", StringComparison.OrdinalIgnoreCase))
        {
            const string kkExportDir = @"D:\Program Files\Koikatu\UserData\cap\OfflineReShade";
            return new PersistedProfilePaths
            {
                ColorPath = Path.Combine(kkExportDir, "coloroutput.png"),
                DepthPath = Path.Combine(kkExportDir, "depthoutput.rfloat"),
                DepthFormat = "raw",
                OutputPath = Path.Combine(kkExportDir, "reshadeoutput.png"),
                GalleryInputFolder = string.Empty,
                GalleryOutputFolder = string.Empty
            };
        }

        return new PersistedProfilePaths
        {
            ColorPath = _paths.DefaultColorPath,
            DepthPath = _paths.DefaultDepthPath,
            DepthFormat = "raw",
            OutputPath = _paths.DefaultOutputPath,
            GalleryInputFolder = string.Empty,
            GalleryOutputFolder = string.Empty
        };
    }

    private static string FirstUsablePath(string? savedPath, string? defaultPath)
    {
        return string.IsNullOrWhiteSpace(savedPath) ? defaultPath ?? string.Empty : savedPath;
    }

    private static bool HasAnyProfilePath(PersistedProfilePaths paths)
    {
        return !string.IsNullOrWhiteSpace(paths.ColorPath) ||
            !string.IsNullOrWhiteSpace(paths.DepthPath) ||
            !string.IsNullOrWhiteSpace(paths.OutputPath) ||
            paths.GalleryInputFolder != null ||
            paths.GalleryOutputFolder != null;
    }

    private static bool IsProfilePathProperty(string? propertyName)
    {
        return propertyName == nameof(SettingsViewModel.ColorPath) ||
            propertyName == nameof(SettingsViewModel.DepthPath) ||
            propertyName == nameof(SettingsViewModel.DepthFormat) ||
            propertyName == nameof(SettingsViewModel.OutputPath) ||
            propertyName == nameof(SettingsViewModel.GalleryInputFolder) ||
            propertyName == nameof(SettingsViewModel.GalleryOutputFolder);
    }

    private static string NormalizeDepthProfile(string? profile)
    {
        return string.Equals(profile, "kk", StringComparison.OrdinalIgnoreCase) ? "kk" : "kks";
    }

    private static string NormalizeDepthFormat(string? format)
    {
        return string.Equals(format, "rgba", StringComparison.OrdinalIgnoreCase) ? "rgba" : "raw";
    }

    private void InferDepthProfileIfNeeded(bool saveIfInferred)
    {
        if (_hasPersistedDepthProfile)
            return;

        var inferred = InferDepthProfileFromPath(Settings.ColorPath) ?? InferDepthProfileFromPath(Settings.DepthPath);
        if (string.IsNullOrEmpty(inferred))
            return;

        _currentDepthProfile = inferred;
        Settings.DepthProfile = inferred;
        _hasPersistedDepthProfile = true;

        if (string.Equals(inferred, "kk", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Settings.DepthFormat, "raw", StringComparison.OrdinalIgnoreCase))
        {
            Settings.DepthFormat = "raw";
        }

        if (saveIfInferred)
            SaveSettingsNow();
    }

    private static string? InferDepthProfileFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (path.IndexOf("KoikatuSunshine", StringComparison.OrdinalIgnoreCase) >= 0)
            return "kks";

        if (path.IndexOf("Koikatu", StringComparison.OrdinalIgnoreCase) >= 0)
            return "kk";

        return null;
    }

    private void ApplyInputSwitchResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return;

        if (result.TryGetProperty("fallbackToKks", out var fallback) && fallback.ValueKind == JsonValueKind.True)
        {
            Settings.DepthProfile = "kks";
            if (result.TryGetProperty("warning", out var warning) && warning.ValueKind == JsonValueKind.String)
                AppendLog(warning.GetString());
        }
    }

    private void OnPrototypeOutputReceived(string? message)
    {
        AppendLog(message);
        if (message?.StartsWith("DEPTH_PROFILE_FALLBACK=KKS", StringComparison.OrdinalIgnoreCase) == true)
            Settings.DepthProfile = "kks";
    }

    private void EnsureGallerySelectionIfNeeded()
    {
        if (IsGalleryMode)
            EnsureGallerySelection();
    }

    private void EnsureGallerySelection()
    {
        if (GalleryItems.Count == 0)
            RefreshGalleryItems();

        if (SelectedGalleryItem?.IsValid == true)
            return;

        SelectedGalleryItem = GalleryItems.FirstOrDefault(item => item.IsValid);
        if (SelectedGalleryItem == null)
            throw new InvalidOperationException("Gallery mode needs a valid *-Color.png + *-Depth.rfloat/png pair, or <frame>.png + <frame>.depth.rfloat pair.");
    }

    private void RefreshGalleryItems()
    {
        var previousBase = SelectedGalleryItem?.BaseName;
        GalleryItems.Clear();

        var inputFolder = Settings.GalleryInputFolder;
        if (string.IsNullOrWhiteSpace(inputFolder) || !Directory.Exists(inputFolder))
        {
            SelectedGalleryItem = null;
            return;
        }

        var outputFolder = string.IsNullOrWhiteSpace(Settings.GalleryOutputFolder)
            ? inputFolder
            : Settings.GalleryOutputFolder;
        var items = new List<GalleryItemViewModel>();
        var seenColorPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var colorPath in Directory.EnumerateFiles(inputFolder, "*.png").Where(IsGalleryColorPath).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            var colorName = Path.GetFileNameWithoutExtension(colorPath);
            var baseName = colorName[..(colorName.Length - "-Color".Length)];
            var rawDepth = FindCaseInsensitiveFile(inputFolder, baseName + "-Depth.rfloat");
            var rgbaDepth = IsKkDepthProfile ? null : FindCaseInsensitiveFile(inputFolder, baseName + "-Depth.png");
            var depthPath = rawDepth ?? rgbaDepth ?? string.Empty;
            var depthFormat = rawDepth != null ? "raw" : "rgba";
            var outputPath = Path.Combine(outputFolder, baseName + "-Reshade.png");
            items.Add(new GalleryItemViewModel(baseName, colorPath, depthPath, depthFormat, outputPath, !string.IsNullOrEmpty(depthPath)));
            seenColorPaths.Add(colorPath);
        }

        foreach (var colorPath in Directory.EnumerateFiles(inputFolder, "*.png").Where(IsVideoFrameColorPath).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!seenColorPaths.Add(colorPath))
                continue;

            var baseName = Path.GetFileNameWithoutExtension(colorPath);
            var rawDepth = FindCaseInsensitiveFile(inputFolder, baseName + ".depth.rfloat");
            var rgbaDepth = IsKkDepthProfile ? null : FindCaseInsensitiveFile(inputFolder, baseName + ".depth.png");
            var depthPath = rawDepth ?? rgbaDepth ?? string.Empty;
            var depthFormat = rawDepth != null ? "raw" : "rgba";
            var outputPath = Path.Combine(outputFolder, baseName + ".reshade.png");
            items.Add(new GalleryItemViewModel(baseName, colorPath, depthPath, depthFormat, outputPath, !string.IsNullOrEmpty(depthPath)));
        }

        foreach (var item in items.OrderBy(static item => item.BaseName, StringComparer.OrdinalIgnoreCase))
            GalleryItems.Add(item);

        SelectedGalleryItem = GalleryItems.FirstOrDefault(item => item.BaseName == previousBase && item.IsValid)
            ?? GalleryItems.FirstOrDefault(item => item.IsValid);
    }

    private static bool IsGalleryColorPath(string path)
    {
        return Path.GetFileNameWithoutExtension(path).EndsWith("-Color", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVideoFrameColorPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return !name.EndsWith("-Color", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith("-Depth", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith("-Reshade", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".depth", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".reshade", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsKkDepthProfile => string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase);

    private static string? FindCaseInsensitiveFile(string folder, string fileName)
    {
        return Directory.EnumerateFiles(folder)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyIfNotNull(string? value, Action<string> apply)
    {
        if (value != null)
            apply(value);
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
        if (!File.Exists(ActiveOutputPath))
        {
            PreviewInfoText = "Full-res ReShade preview";
            return;
        }

        PreviewInfoText = Path.GetFileName(ActiveOutputPath);
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
        BatchApplyCommand.RaiseCanExecuteChanged();
    }

    private async Task<TimeSpan> BuildFrameDelayAsync(int frames)
    {
        if (frames <= 0)
            return TimeSpan.Zero;

        var fps = 60.0;
        try
        {
            if (_rpc.IsConnected)
            {
                var info = await _rpc.CallAsync("get_runtime_info");
                if (info.TryGetProperty("renderFps", out var renderFps))
                    fps = Math.Max(1.0, renderFps.GetDouble());
            }
        }
        catch (Exception ex)
        {
            AppendLog("Batch frame delay uses 60 FPS fallback: " + ex.Message);
        }

        return TimeSpan.FromMilliseconds(Math.Ceiling(frames * 1000.0 / fps));
    }

    private static int ParseNonNegativeInt(string text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : fallback;
    }

    public void Dispose()
    {
        _isDisposed = true;
        var settingsSaveCancellation = _settingsSaveCancellation;
        _settingsSaveCancellation = null;
        settingsSaveCancellation?.Cancel();
        settingsSaveCancellation?.Dispose();
        try
        {
            SaveSettingsNow();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        _prototype.OutputReceived -= OnPrototypeOutputReceived;
        StopFpsPolling();
        _rpc.Dispose();
        _previewFrames.Dispose();
        _prototype.Dispose();
    }
}
