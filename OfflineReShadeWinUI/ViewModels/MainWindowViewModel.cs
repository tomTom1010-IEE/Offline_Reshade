using System.Collections.ObjectModel;
using OfflineReShade.Core;
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
    private readonly NativeSession _session;
    private readonly UserSettingsStore _settingsStore;
    private readonly AutomationEngine _automation;
    private readonly AutomationBroker _broker;
    private readonly string _workDir = Path.Combine(AutomationBroker.SessionDirectory, "work", Guid.NewGuid().ToString("N"));
    private RenderInput? _inputOverride;
    private RenderInput? _runtimeInput => Settings.Session.Applied?.Input;
    private bool _cpuPreviewFallback;
    private string _lastPreviewError = "";
    public bool IsGpuPreviewActive => !UseCpuPreview && !_cpuPreviewFallback;
    public ulong AdapterLuid { get; private set; }
    public uint RenderWidth { get; private set; }
    public uint RenderHeight { get; private set; }
    public bool CanEditSession => !IsBatchApplying;
    public string AutomationSessionText => "MCP session: " + _broker.Descriptor.Id;
    public string AutomationSessionId => _broker.Descriptor.Id;
    public double BatchProgress => _automation.Job?.Completed ?? 0;
    public double BatchMaximum => Math.Max(1, _automation.Job?.Total ?? 1);
    public string BatchStatusText => _automation.Job is { } j ? $"{j.State}: {j.Completed}/{j.Total}" + (j.Error == null ? "" : " — " + j.Error) : "Ready";
    public RelayCommand CancelBatchCommand { get; }

    private readonly PreviewFrameClient _previewFrames = new();
    private readonly SynchronizationContext? _syncContext;
    private CancellationTokenSource? _fpsCancellation;
    private CancellationTokenSource? _settingsSaveCancellation;
    private SettingsPickerService? _picker;
    private bool _isPreviewRunning;
    private int _pendingOpens;
    private bool _isSettingsOpen;
    private bool _isBatchApplying;
    private bool _effectsEnabled = true;
    private string _statusText = "Ready";
    private string _controlStatusText = "Disconnected";
    private string _previewInfoText = "Full-res ReShade preview";
    private string _fpsText = string.Empty;
    private string _logText = string.Empty;
    private string _addonDebugText = "No ImGui controls captured yet.";
    private string _addonSearchPath = string.Empty;
    private string _addonLogPath = string.Empty;
    private bool _allAddonsLoaded = true;
    private bool _isGalleryPanelOpen = true;
    private GalleryItemViewModel? _selectedGalleryItem;
    private ulong _sharedPreviewHandle;
    private uint _sharedPreviewWidth;
    private uint _sharedPreviewHeight;
    private ulong _sharedAddonOverlayHandle;
    private uint _sharedAddonOverlayWidth;
    private uint _sharedAddonOverlayHeight;
    private bool _isRestoringSettings;
    private bool _isDisposed;
    private bool _controlStatePopulated;
    private bool _isRefreshingControlState;
    private int _runtimeGeneration;
    private bool _isEffectsLoading;
    private DateTimeOffset? _effectsLoadingStartedAt;
    private bool _hasPersistedDepthProfile;
    private bool _isApplyingProfilePaths;
    private string _currentDepthProfile = "kks";

    public MainWindowViewModel(AppPaths paths, SynchronizationContext? uiContext = null)
    {
        _paths = paths;
        _settingsStore = new UserSettingsStore(paths);
        Settings = new SettingsViewModel(paths);
        RestorePersistedSettings();
        _currentDepthProfile = NormalizeDepthProfile(Settings.DepthProfile);
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        Settings.Session.Changed += OnEditingSessionChanged;
        NormalizeDepthSettings();
        InferDepthProfileIfNeeded(saveIfInferred: true);
        InitializeProfilePathState();
        _session = new NativeSession(paths.PrototypePath, new(_workDir, PreviewPipe: true, AddonOverlay: true), Settings.Session);
        _session.OutputReceived += OnPrototypeOutputReceived;
        _session.UnexpectedExit += OnPreviewExited;
        _session.RuntimeResetting += ResetPreviewPresentation;
        _session.PreviewPipeCreated += _previewFrames.Start;
        _previewFrames.FrameReceived += frame => PreviewFrameReceived?.Invoke(frame);
        _previewFrames.ErrorReceived += AppendLog;
        _syncContext = uiContext ?? SynchronizationContext.Current;

        _automation = new AutomationEngine((m, a, t) => _session.CallAsync(m, a, t), OpenAutomationInputAsync, () => _session.Current);
        _automation.Changed += OnAutomationChanged;
        _broker = new AutomationBroker(DispatchAutomationAsync, "Offline ReShade WinUI " + BuildContract.ProductVersion);

        StartPreviewCommand = new AsyncRelayCommand(StartPreviewAsync, () => !IsPreviewRunning && CanEditSession);
        StopPreviewCommand = new AsyncRelayCommand(StopPreviewAsync, () => IsPreviewRunning || _pendingOpens > 0);
        CancelBatchCommand = new RelayCommand(() => _automation.Cancel(), () => _automation.Job?.State == "running");
        SavePngCommand = new AsyncRelayCommand(SavePngAsync, () => CanEditSession);
        ReShadeShotCommand = new AsyncRelayCommand(ReShadeShotAsync, () => _session.IsConnected && CanEditSession);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => _session.IsConnected && CanEditSession);
        SavePresetCommand = new AsyncRelayCommand(SavePresetAsync, () => _session.IsConnected && CanEditSession);
        ToggleInputModeCommand = new AsyncRelayCommand(ToggleInputModeAsync, () => CanEditSession);
        ToggleGalleryPanelCommand = new RelayCommand(() => IsGalleryPanelOpen = !IsGalleryPanelOpen);
        RefreshGalleryCommand = new RelayCommand(RefreshGalleryItems, () => CanEditSession);
        BatchApplySavePngCommand = new AsyncRelayCommand(() => BatchApplyGalleryAsync("save_output", "Save PNG"), () => IsGalleryMode && _session.IsConnected && !IsBatchApplying);
        BatchApplyReShadeShotCommand = new AsyncRelayCommand(() => BatchApplyGalleryAsync("save_screenshot", "ReShade Shot"), () => IsGalleryMode && _session.IsConnected && !IsBatchApplying);
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
    public ObservableCollection<AddonViewModel> Addons { get; } = new();
    public ObservableCollection<AddonDiagnosticViewModel> AddonDiagnostics { get; } = new();
    public ObservableCollection<string> AddonLogErrors { get; } = new();
    public ObservableCollection<AddonOverlayViewModel> AddonOverlays { get; } = new();
    public ObservableCollection<AddonImGuiControlViewModel> AddonImGuiControls { get; } = new();
    public ObservableCollection<GalleryItemViewModel> GalleryItems { get; } = new();

    public AsyncRelayCommand StartPreviewCommand { get; }
    public AsyncRelayCommand StopPreviewCommand { get; }
    public AsyncRelayCommand SavePngCommand { get; }
    public AsyncRelayCommand ReShadeShotCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand SavePresetCommand { get; }
    public AsyncRelayCommand ToggleInputModeCommand { get; }
    public RelayCommand ToggleGalleryPanelCommand { get; }
    public RelayCommand RefreshGalleryCommand { get; }
    public AsyncRelayCommand BatchApplySavePngCommand { get; }
    public AsyncRelayCommand BatchApplyReShadeShotCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public AsyncRelayCommand PickColorCommand { get; }
    public AsyncRelayCommand PickDepthCommand { get; }
    public AsyncRelayCommand PickEffectDirCommand { get; }
    public AsyncRelayCommand PickPresetCommand { get; }
    public AsyncRelayCommand PickOutputCommand { get; }
    public AsyncRelayCommand PickGalleryInputFolderCommand { get; }
    public AsyncRelayCommand PickGalleryOutputFolderCommand { get; }

    public event Action? ControlsChanged;
    public event Action? AddonControlsChanged;
    public event Action? AddonControlValuesChanged;
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
    public bool IsEffectsLoading
    {
        get => _isEffectsLoading;
        private set
        {
            if (SetProperty(ref _isEffectsLoading, value))
                OnPropertyChanged(nameof(EffectsLoadingOpacity));
        }
    }
    public double EffectsLoadingOpacity => IsEffectsLoading ? 1.0 : 0.0;
    public string PreviewInfoText { get => _previewInfoText; private set => SetProperty(ref _previewInfoText, value); }
    public string FpsText { get => _fpsText; private set => SetProperty(ref _fpsText, value); }
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }
    public string AddonDebugText { get => _addonDebugText; private set => SetProperty(ref _addonDebugText, value); }
    public string AddonSearchPath { get => _addonSearchPath; private set => SetProperty(ref _addonSearchPath, value); }
    public string AddonLogPath { get => _addonLogPath; private set => SetProperty(ref _addonLogPath, value); }
    public bool AllAddonsLoaded { get => _allAddonsLoaded; private set => SetProperty(ref _allAddonsLoaded, value); }
    public string InputMode
    {
        get => Settings.InputMode;
        private set
        {
            if (Settings.InputMode != value)
            {
                Settings.InputMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGalleryMode));
                OnPropertyChanged(nameof(InputModeButtonText));
                RaiseBatchApplyCanExecuteChanged();
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
            if (SetProperty(ref _isBatchApplying, value)) {
                OnPropertyChanged(nameof(CanEditSession));
                RaiseControlCommandStates();
                StartPreviewCommand.RaiseCanExecuteChanged();
                SavePngCommand.RaiseCanExecuteChanged();
                ToggleInputModeCommand.RaiseCanExecuteChanged();
                RefreshGalleryCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public bool IsGalleryPanelOpen { get => _isGalleryPanelOpen; set => SetProperty(ref _isGalleryPanelOpen, value); }
    public GalleryItemViewModel? SelectedGalleryItem { get => _selectedGalleryItem; private set => SetProperty(ref _selectedGalleryItem, value); }
    public ulong SharedPreviewHandle { get => _sharedPreviewHandle; private set => SetProperty(ref _sharedPreviewHandle, value); }
    public uint SharedPreviewWidth { get => _sharedPreviewWidth; private set => SetProperty(ref _sharedPreviewWidth, value); }
    public uint SharedPreviewHeight { get => _sharedPreviewHeight; private set => SetProperty(ref _sharedPreviewHeight, value); }
    public ulong SharedAddonOverlayHandle { get => _sharedAddonOverlayHandle; private set => SetProperty(ref _sharedAddonOverlayHandle, value); }
    public uint SharedAddonOverlayWidth { get => _sharedAddonOverlayWidth; private set => SetProperty(ref _sharedAddonOverlayWidth, value); }
    public uint SharedAddonOverlayHeight { get => _sharedAddonOverlayHeight; private set => SetProperty(ref _sharedAddonOverlayHeight, value); }

    public void Initialize(SettingsPickerService picker)
    {
        _picker = picker;
    }

    private void RestorePersistedSettings()
    {
        var settings = _settingsStore.Load(new WorkspaceConfiguration { Editor = Settings.Editor with { DepthProfile = "" }, Preferences = Settings.Preferences });
        if (_settingsStore.LoadError != null) AppendLog(_settingsStore.LoadError);
        if (settings == null)
            return;

        Directory.CreateDirectory(_workDir);
        if (!string.IsNullOrEmpty(settings.SessionPresetPath) && File.Exists(settings.SessionPresetPath))
            File.Copy(settings.SessionPresetPath, Path.Combine(_workDir, "OfflinePreset.ini"), true);

        _isRestoringSettings = true;
        try
        {
            ApplyIfNotNull(settings.Editor.ColorPath, value => Settings.ColorPath = value);
            ApplyIfNotNull(settings.Editor.DepthPath, value => Settings.DepthPath = value);
            ApplyIfNotNull(settings.Editor.DepthFormat, value => Settings.DepthFormat = value);
            if (!string.IsNullOrWhiteSpace(settings.Editor.DepthProfile))
            {
                Settings.DepthProfile = settings.Editor.DepthProfile;
                _hasPersistedDepthProfile = true;
            }
            ApplyIfNotNull(settings.Editor.DepthDownsample, value => Settings.DepthDownsample = value);
            ApplyIfNotNull(settings.Editor.EffectDir, value => Settings.EffectDir = value);
            ApplyIfNotNull(settings.Editor.PresetPath, value => Settings.PresetPath = value);
            ApplyIfNotNull(settings.Editor.OutputPath, value => Settings.OutputPath = value);
            ApplyIfNotNull(settings.Editor.RenderWidth, value => Settings.RenderWidth = value);
            ApplyIfNotNull(settings.Editor.RenderHeight, value => Settings.RenderHeight = value);
            ApplyIfNotNull(settings.Preferences.PreviewTransport, value => Settings.PreviewTransport = value);
            ApplyIfNotNull(settings.Preferences.GalleryInputFolder, value => Settings.GalleryInputFolder = value);
            ApplyIfNotNull(settings.Preferences.GalleryOutputFolder, value => Settings.GalleryOutputFolder = value);
            ApplyIfNotNull(settings.Preferences.GalleryBatchFrameDelay, value => Settings.GalleryBatchFrameDelay = value);
            Settings.ShowFps = settings.Preferences.ShowFps;
            Settings.SliderDragSensitivity = settings.Preferences.SliderDragSensitivity;
            Settings.SliderSymLog = settings.Preferences.SliderSymLog;
            Settings.KksPaths = settings.Preferences.KksPaths ?? new ProfilePaths();
            Settings.KkPaths = settings.Preferences.KkPaths ?? new ProfilePaths();
            if (string.Equals(settings.Editor.InputMode, "Gallery", StringComparison.OrdinalIgnoreCase))
                Settings.InputMode = "Gallery";
            else if (string.Equals(settings.Editor.InputMode, "RealTime", StringComparison.OrdinalIgnoreCase))
                Settings.InputMode = "RealTime";
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
        if (!CanEditSession) return;
        await _session.CallAsync("set_effects_state", new { enabled });
        EffectsEnabled = enabled;
    }

    public async Task SetTechniqueStateAsync(TechniqueViewModel technique, bool enabled)
    {
        if (!CanEditSession) return;
        technique.IsEnabled = enabled;
        await _session.CallAsync("set_technique_state", new { id = technique.Id, enabled });
        await RefreshControlStateAsync();
    }

    public async Task ReorderTechniquesAsync(IReadOnlyList<TechniqueViewModel> techniques)
    {
        if (!CanEditSession) return;
        if (!_session.IsConnected || techniques.Count == 0)
            return;

        var ids = new string[techniques.Count];
        for (var i = 0; i < techniques.Count; ++i)
            ids[i] = techniques[i].Id;

        try
        {
            await _session.CallAsync("reorder_techniques", new { ids });
        }
        catch
        {
            await RefreshControlStateAsync();
            throw;
        }
    }

    public IReadOnlyList<string> MoveEffect(string draggedEffectName, string targetEffectName, bool insertAfter)
    {
        var effects = Effects.ToList();
        var from = effects.FindIndex(effect => string.Equals(effect.Name, draggedEffectName, StringComparison.OrdinalIgnoreCase));
        var to = effects.FindIndex(effect => string.Equals(effect.Name, targetEffectName, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || to < 0 || from == to)
            return effects.Select(static effect => effect.Name).ToArray();

        var moving = effects[from];
        effects.RemoveAt(from);
        if (from < to)
            --to;
        if (insertAfter)
            ++to;

        to = Math.Clamp(to, 0, effects.Count);
        effects.Insert(to, moving);

        Effects.Clear();
        foreach (var effect in effects)
            Effects.Add(effect);

        return effects.Select(static effect => effect.Name).ToArray();
    }

    public async Task ReorderEnabledEffectsAsync(IReadOnlyList<string> orderedEffectNames)
    {
        if (!CanEditSession) return;
        if (!_session.IsConnected || orderedEffectNames.Count == 0)
            return;

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var orderedTechniques = new List<TechniqueViewModel>();

        foreach (var effectName in orderedEffectNames)
        {
            foreach (var technique in Techniques)
            {
                if (!technique.IsEnabled ||
                    !string.Equals(technique.EffectName, effectName, StringComparison.OrdinalIgnoreCase) ||
                    !ids.Add(technique.Id))
                    continue;

                orderedTechniques.Add(technique);
            }
        }

        foreach (var technique in Techniques)
        {
            if (technique.IsEnabled && ids.Add(technique.Id))
                orderedTechniques.Add(technique);
        }

        await ReorderTechniquesAsync(orderedTechniques);
    }

    public async Task SetUniformAsync(UniformViewModel uniform, object value)
    {
        if (!CanEditSession) return;
        await _session.CallAsync("set_uniform", new { id = uniform.Id, value });
    }

    public async Task SetPreprocessorDefinitionAsync(PreprocessorDefinitionViewModel definition, string value)
    {
        if (!CanEditSession) return;
        definition.Value = value;
        MarkEffectsLoading();
        await _session.CallAsync("set_preprocessor_definition", new { effectName = definition.EffectName, name = definition.Name, value });
        await Task.Delay(500);
        await RefreshControlStateAsync();
    }

    public async Task SetAddonImGuiValueAsync(AddonImGuiControlViewModel control, string value)
    {
        if (!CanEditSession) return;
        await _session.CallAsync("set_addon_imgui_value", new { id = control.Id, value });
    }

    public async Task OpenNativeAddonPanelAsync()
    {
        if (!CanEditSession) return;
        await _session.CallAsync("open_native_addon_panel");
    }

    public async Task SelectNativeAddonOverlayAsync(AddonOverlayViewModel? overlay)
    {
        if (!CanEditSession) return;
        await _session.CallAsync("set_native_addon_overlay", new
        {
            enabled = overlay != null,
            addon = overlay?.AddonName ?? string.Empty,
            overlay = overlay?.Title ?? string.Empty,
            settings = overlay?.IsSettings ?? false
        });
    }

    public async Task SendAddonOverlayInputAsync(PreviewHostInputMessage input)
    {
        if (!CanEditSession) return;
        if (!_session.IsConnected)
            return;

        try
        {
            await _session.CallAsync("send_addon_overlay_input", new
            {
                message = unchecked((int)input.Message),
                wparam = unchecked((int)input.WParam),
                lparam = unchecked((int)input.LParam)
            });
        }
        catch
        {
            // Input is transient; connection failures are reported by the normal preview status path.
        }
    }

    public async Task SetAddonOverlaySurfaceSizeAsync(uint width, uint height)
    {
        if (!_session.IsConnected || width == 0 || height == 0)
            return;

        await _session.CallAsync("set_addon_overlay_size", new { width, height });
    }

    public async Task SelectGalleryItemAsync(GalleryItemViewModel? item)
    {
        if (!CanEditSession || item == null || !item.IsValid) return;
        SelectedGalleryItem = item;
        _inputOverride = null;
        PreviewInfoText = item.BaseName;
        if (IsGalleryMode && _session.IsConnected)
            await OpenAutomationInputAsync(InputForGallery(item), CancellationToken.None);
    }

    public async Task ToggleInputModeAsync()
    {
        if (!CanEditSession) return;
        var previous = InputMode;
        try {
            InputMode = IsGalleryMode ? "RealTime" : "Gallery";
            _inputOverride = null;
            if (IsGalleryMode) { RefreshGalleryItems(); EnsureGallerySelection(); }
            if (_session.IsConnected) {
                await OpenAutomationInputAsync(CurrentRenderInput(), CancellationToken.None);
                await _session.CallAsync("set_input_watch_enabled", new { enabled = !IsGalleryMode });
            }
        } catch (Exception ex) { InputMode = previous; AppendLog(ex.Message); }
    }

    private Task StartPreviewAsync() => StartPreviewCoreAsync(false);

    private async Task StartPreviewCoreAsync(bool preserveBatch)
    {
        try
        {
            if (IsGalleryMode && _inputOverride == null) EnsureGallerySelection();
            ControlStatusText = "Connecting";
            await OpenAutomationInputAsync(CurrentRenderInput(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            StatusText = "Failed";
            ControlStatusText = _session.IsConnected ? "Connected" : "Control failed";
            if (preserveBatch) throw;
        }
    }

    private Task StopPreviewAsync() => StopPreviewAsync(updateStatus: true);
    private async Task StopPreviewAsync(bool updateStatus)
    {
        _automation.Cancel(restore: false);
        await _automation.StopAsync();
        await _session.StopAsync();
        if (updateStatus) StatusText = "Preview stopped";
    }

    private void ResetPreviewPresentation()
    {
        _runtimeGeneration++;
        StopFpsPolling();
        _previewFrames.Stop();
        _cpuPreviewFallback = false;
        _lastPreviewError = "";
        OnPropertyChanged(nameof(IsGpuPreviewActive));
        IsPreviewRunning = false;
        ControlStatusText = "Disconnected";
        _controlStatePopulated = false;
        CompleteEffectsLoading();
        FpsText = string.Empty;
        SharedPreviewHandle = 0;
        SharedPreviewWidth = 0;
        SharedPreviewHeight = 0;
        SharedAddonOverlayHandle = 0;
        SharedAddonOverlayWidth = 0;
        SharedAddonOverlayHeight = 0;
        RaiseControlCommandStates();
        Techniques.Clear();
        Effects.Clear();
        Addons.Clear();
        AddonImGuiControls.Clear();
        AddonDebugText = "No ImGui controls captured yet.";
        ControlsChanged?.Invoke();
        AddonControlsChanged?.Invoke();
    }

    private Task SavePngAsync() => SaveImageAsync(false);
    private Task ReShadeShotAsync() => SaveImageAsync(true);
    private async Task SaveImageAsync(bool reShadeShot)
    {
        try {
            if (!_session.IsConnected) await StartPreviewCoreAsync(false);
            StatusText = "Rendering";
            var saved = await _automation.ExecuteAsync("export_image", AutomationEngine.Json(new { path = ActiveOutputPath, reShadeShot, warmupFrames = 5 }));
            if (saved.TryGetProperty("warning", out var warning) && !string.IsNullOrEmpty(warning.GetString())) AppendLog(warning.GetString()!);
            RefreshOutputInfo(); StatusText = "Saved";
        } catch (Exception ex) { AppendLog(ex.Message); StatusText = "Failed"; }
    }

    private async Task BatchApplyGalleryAsync(string saveMethod, string label)
    {
        try {
            if (!IsGalleryMode || !_session.IsConnected) throw new InvalidOperationException("Start Gallery preview before batch export.");
            RefreshGalleryItems();
            var items = GalleryItems.Where(x => x.IsValid).Select(InputForGallery).ToArray();
            var frames = ParseNonNegativeInt(Settings.GalleryBatchFrameDelay, 5);
            await _automation.ExecuteAsync("batch_start", AutomationEngine.Json(new BatchRequest(items, frames, saveMethod == "save_screenshot")));
            AppendLog($"Batch {label} started; existing validated outputs will be resumed.");
        } catch (Exception ex) { AppendLog(ex.Message); StatusText = "Batch failed"; }
    }

    private RenderInput CurrentRenderInput()
    {
        if (_inputOverride != null) return _inputOverride;
        Settings.Editor.ToRenderInput();
        return new RenderInput(ActiveColorPath, ActiveDepthPath, ActiveDepthFormat,
        ActiveDepthProfile, Settings.DepthDownsample, ActiveOutputPath, Settings.EffectDir, Settings.PresetPath,
        EditorDocument.ParseDimension(Settings.RenderWidth, "Width"), EditorDocument.ParseDimension(Settings.RenderHeight, "Height"), WatchInput: !IsGalleryMode);
    }
    private RenderInput InputForGallery(GalleryItemViewModel item) => CurrentRenderInput() with {
        ColorPath = item.ColorPath, DepthPath = item.DepthPath, DepthFormat = item.DepthFormat,
        DepthProfile = EffectiveDepthProfile(item.DepthFormat), OutputPath = item.OutputPath, WatchInput = false };

    private async Task OpenAutomationInputAsync(RenderInput input, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _pendingOpens++;
        StopPreviewCommand.RaiseCanExecuteChanged();
        try
        {
            await _session.OpenAsync(input, token);
            _inputOverride = _session.Current;
            PreviewInfoText = Path.GetFileName(_session.Current.ColorPath);
            var item = GalleryItems.FirstOrDefault(x => string.Equals(x.ColorPath, _session.Current.ColorPath, StringComparison.OrdinalIgnoreCase));
            if (item != null) SelectedGalleryItem = item;
        }
        finally
        {
            _pendingOpens--;
            StopPreviewCommand.RaiseCanExecuteChanged();
            if (!_isDisposed && _session.IsConnected)
            {
                await _session.CallAsync("set_preview_transport", new { cpu = UseCpuPreview });
                IsPreviewRunning = true;
                ControlStatusText = "Connected";
                StatusText = "Preview running";
                RaiseControlCommandStates();
                await RefreshControlStateAsync();
                StartFpsPolling();
            }
        }
    }

    private Task<JsonElement> DispatchAutomationAsync(string method, JsonElement args, CancellationToken token)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Execute(object? _) {
            try {
                var result = await _automation.ExecuteAsync(method, args, token);
                if (method is "set_parameters" or "set_uniform" or "reset_uniform" or "set_technique_state" or "reorder_techniques" or "set_preset" or "set_effects_state") await RefreshControlStateAsync();
                completion.TrySetResult(result);
            } catch (Exception ex) { completion.TrySetException(ex); }
        }
        if (_syncContext != null) _syncContext.Post(Execute, null); else Execute(null);
        return completion.Task;
    }

    private void OnAutomationChanged()
    {
        IsBatchApplying = _automation.IsBusy;
        OnPropertyChanged(nameof(BatchProgress)); OnPropertyChanged(nameof(BatchMaximum)); OnPropertyChanged(nameof(BatchStatusText));
        CancelBatchCommand.RaiseCanExecuteChanged();
        if (_automation.Job is { } job) { StatusText = $"Batch {job.State}: {job.Completed}/{job.Total}"; if (job.Error != null) AppendLog(job.Error); }
    }

    private async Task ReloadAsync()
    {
        MarkEffectsLoading();
        await _session.CallAsync("reload_effects");
        await Task.Delay(500);
        await RefreshControlStateAsync();
    }

    private async Task SavePresetAsync()
    {
        await _session.CallAsync("save_preset");
        AppendLog("Preset saved.");
    }

    private async Task RefreshControlStateAsync()
    {
        if (_isRefreshingControlState)
            return;

        _isRefreshingControlState = true;
        var generation = _runtimeGeneration;
        try
        {
            var runtimeInfo = await _session.CallAsync("get_runtime_info");
            if (_isDisposed || generation != _runtimeGeneration) return;
            await ApplyGraphicsStateAsync(runtimeInfo);
            if (_isDisposed || generation != _runtimeGeneration) return;
            if (IsRuntimeLoading(runtimeInfo))
            {
                MarkEffectsLoading();
                return;
            }

            var state = await _session.CallAsync("list_state");
            if (_isDisposed || generation != _runtimeGeneration) return;
            if (IsRuntimeLoading(state))
            {
                MarkEffectsLoading();
                return;
            }

            var techniquesList = JsonStateParser.ParseTechniques(state);
            var uniformsList = JsonStateParser.ParseUniforms(state);
            var definitionsList = JsonStateParser.ParsePreprocessorDefinitions(state);
            var addonsList = JsonStateParser.ParseAddons(state);
            var addonDiagnostics = JsonStateParser.ParseAddonDiagnostics(state);
            var addonLogErrors = JsonStateParser.ParseAddonLogErrors(state);
            AddonSearchPath = JsonStateParser.ParseAddonSearchPath(state);
            AddonLogPath = JsonStateParser.ParseAddonLogPath(state);
            AllAddonsLoaded = JsonStateParser.ParseAllAddonsLoaded(state);
            if (state.TryGetProperty("runtime", out var runtimeState))
            {
                SharedPreviewHandle = runtimeState.TryGetProperty("sharedPreviewHandle", out var sharedPreviewHandle) ? sharedPreviewHandle.GetUInt64() : 0;
                SharedPreviewWidth = runtimeState.TryGetProperty("sharedPreviewWidth", out var sharedPreviewWidth) ? sharedPreviewWidth.GetUInt32() : 0;
                SharedPreviewHeight = runtimeState.TryGetProperty("sharedPreviewHeight", out var sharedPreviewHeight) ? sharedPreviewHeight.GetUInt32() : 0;
                SharedAddonOverlayHandle = runtimeState.TryGetProperty("sharedAddonOverlayHandle", out var sharedAddonOverlayHandle) ? sharedAddonOverlayHandle.GetUInt64() : 0;
                SharedAddonOverlayWidth = runtimeState.TryGetProperty("sharedAddonOverlayWidth", out var sharedAddonOverlayWidth) ? sharedAddonOverlayWidth.GetUInt32() : 0;
                SharedAddonOverlayHeight = runtimeState.TryGetProperty("sharedAddonOverlayHeight", out var sharedAddonOverlayHeight) ? sharedAddonOverlayHeight.GetUInt32() : 0;
            }
            ApplyAddonUiState(state);
            EffectsEnabled = JsonStateParser.ParseEffectsEnabled(state);

            Techniques.Clear();
            foreach (var technique in techniquesList)
                Techniques.Add(technique);

            Effects.Clear();
            foreach (var effect in JsonStateParser.BuildEffects(techniquesList, uniformsList, definitionsList))
                Effects.Add(effect);

            Addons.Clear();
            foreach (var addon in addonsList)
                Addons.Add(addon);

            AddonOverlays.Clear();
            foreach (var addon in addonsList.Where(static addon => addon.IsLoaded))
            {
                if (addon.HasSettingsOverlay)
                    AddonOverlays.Add(new AddonOverlayViewModel(addon.Name, "Settings", true));
                if (addon.HasEventOverlay)
                    AddonOverlays.Add(new AddonOverlayViewModel(addon.Name, "@event", false, "Event Overlay"));
                foreach (var overlay in addon.Overlays.Where(static overlay => !string.Equals(overlay, "OSD", StringComparison.OrdinalIgnoreCase)))
                    AddonOverlays.Add(new AddonOverlayViewModel(addon.Name, overlay, false));
            }

            AddonDiagnostics.Clear();
            foreach (var diagnostic in addonDiagnostics)
                AddonDiagnostics.Add(diagnostic);

            AddonLogErrors.Clear();
            foreach (var logError in addonLogErrors)
                AddonLogErrors.Add(logError);

            var hasLoadedEffectControls = techniquesList.Count != 0 || uniformsList.Count != 0 || definitionsList.Count != 0;
            _controlStatePopulated = true;
            CompleteEffectsLoading();
            ControlStatusText = hasLoadedEffectControls ? "Ready" : "No effects";
            RaiseControlCommandStates();
            ControlsChanged?.Invoke();
        }
        finally
        {
            _isRefreshingControlState = false;
        }
    }

    private void MarkEffectsLoading()
    {
        _controlStatePopulated = false;
        if (!IsEffectsLoading)
        {
            _effectsLoadingStartedAt = DateTimeOffset.UtcNow;
            IsEffectsLoading = true;
        }

        var elapsedSeconds = _effectsLoadingStartedAt.HasValue
            ? Math.Max(0, (int)(DateTimeOffset.UtcNow - _effectsLoadingStartedAt.Value).TotalSeconds)
            : 0;
        ControlStatusText = elapsedSeconds == 0
            ? "Loading effects"
            : string.Create(CultureInfo.InvariantCulture, $"Loading effects ({elapsedSeconds}s)");
    }

    private void CompleteEffectsLoading()
    {
        _effectsLoadingStartedAt = null;
        IsEffectsLoading = false;
    }

    private static bool IsRuntimeLoading(JsonElement state)
    {
        if (state.TryGetProperty("loading", out var loading))
            return loading.ValueKind == JsonValueKind.True;

        return state.TryGetProperty("runtime", out var runtime) &&
            runtime.TryGetProperty("loading", out loading) &&
            loading.ValueKind == JsonValueKind.True;
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

    private void ApplyAddonUiState(JsonElement state)
    {
        AddonDebugText = JsonStateParser.ParseAddonUiDebugText(state);

        var controls = JsonStateParser.ParseAddonImGuiControls(state);
        var oldSignature = string.Join('\n', AddonImGuiControls.Select(AddonControlStructureSignature));
        var newSignature = string.Join('\n', controls.Select(AddonControlStructureSignature));
        if (string.Equals(oldSignature, newSignature, StringComparison.Ordinal))
        {
            var valuesChanged = false;
            for (var i = 0; i < controls.Count; ++i)
                valuesChanged |= AddonImGuiControls[i].UpdateValue(controls[i].Value);

            if (valuesChanged)
                AddonControlValuesChanged?.Invoke();
            return;
        }

        AddonImGuiControls.Clear();
        foreach (var control in controls)
            AddonImGuiControls.Add(control);
        AddonControlsChanged?.Invoke();
    }

    private static string AddonControlStructureSignature(AddonImGuiControlViewModel control)
    {
        return string.Join('\u001F',
            control.Id,
            control.Kind,
            control.Label,
            control.Minimum,
            control.Maximum,
            control.Components.ToString(CultureInfo.InvariantCulture),
            string.Join('\u001E', control.Items));
    }

    private async Task PollFpsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_session.IsConnected)
                {
                    var info = await _session.CallAsync("get_runtime_info", cancellationToken: cancellationToken);
                    if (_isDisposed || cancellationToken.IsCancellationRequested) return;
                    var needsRecovery = info.TryGetProperty("recoveryRequired", out var recovery) && recovery.GetBoolean();
                    if (CanEditSession && (needsRecovery || info.TryGetProperty("restartRequired", out var restart) && restart.GetBoolean())) {
                        if (needsRecovery) AppendLog("D3D device lost; rebuilding the rendering session.");
                        await _automation.ExecuteAsync("open_input", AutomationEngine.Json(_session.Current), CancellationToken.None);
                        return; // The replacement runtime owns a new polling loop.
                    }
                    if (needsRecovery) { await Task.Delay(100, cancellationToken); continue; } // The active export/batch owns recovery.
                    await ApplyGraphicsStateAsync(info);
                    if (_isDisposed || cancellationToken.IsCancellationRequested) return;
                    var renderFps = info.TryGetProperty("renderFps", out var render) ? render.GetDouble() : 0.0;
                    var previewFps = info.TryGetProperty("previewFps", out var preview) ? preview.GetDouble() : 0.0;
                    SharedPreviewHandle = info.TryGetProperty("sharedPreviewHandle", out var handle) ? handle.GetUInt64() : 0;
                    SharedPreviewWidth = info.TryGetProperty("sharedPreviewWidth", out var previewWidth) ? previewWidth.GetUInt32() : 0;
                    SharedPreviewHeight = info.TryGetProperty("sharedPreviewHeight", out var previewHeight) ? previewHeight.GetUInt32() : 0;
                    SharedAddonOverlayHandle = info.TryGetProperty("sharedAddonOverlayHandle", out var addonOverlayHandle) ? addonOverlayHandle.GetUInt64() : 0;
                    SharedAddonOverlayWidth = info.TryGetProperty("sharedAddonOverlayWidth", out var addonOverlayWidth) ? addonOverlayWidth.GetUInt32() : 0;
                    SharedAddonOverlayHeight = info.TryGetProperty("sharedAddonOverlayHeight", out var addonOverlayHeight) ? addonOverlayHeight.GetUInt32() : 0;
                    FpsText = Settings.ShowFps
                        ? string.Create(CultureInfo.InvariantCulture, $"Runtime {renderFps:0.0} FPS | Preview {previewFps:0.0} FPS")
                        : string.Empty;
                    var isLoading = IsRuntimeLoading(info);
                    if (isLoading)
                    {
                        MarkEffectsLoading();
                    }
                    else if ((IsEffectsLoading || !_controlStatePopulated) && !_isRefreshingControlState)
                    {
                        await RefreshControlStateAsync();
                    }
                    var addonUi = await _session.CallAsync("list_addon_imgui_capture", cancellationToken: cancellationToken);
                    if (_isDisposed || cancellationToken.IsCancellationRequested) return;
                    ApplyAddonUiState(addonUi);
                }
                else
                {
                    FpsText = string.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (DeviceLostException ex)
            {
                AppendLog(ex.Message); StatusText = "D3D recovery failed";
                await StopPreviewAsync(updateStatus: false);
                return;
            }
            catch (Exception ex)
            {
                if (_isDisposed || cancellationToken.IsCancellationRequested) return;
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

    public bool HasPendingConfiguration
    {
        get { try { return Settings.Session.ValidationError != null || _runtimeInput == null || CurrentRenderInput() != _runtimeInput; } catch (ArgumentException) { return true; } }
    }
    public string ConfigurationStatusText => Settings.Session.ValidationError ?? (HasPendingConfiguration ? "Changes pending - apply before export" : "Configuration applied");
    private void OnEditingSessionChanged()
    {
        OnPropertyChanged(nameof(HasPendingConfiguration));
        OnPropertyChanged(nameof(ConfigurationStatusText));
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (CanEditSession && e.PropertyName is "ColorPath" or "DepthPath" or "DepthFormat" or "EffectDir" or "PresetPath" or "RenderWidth" or "RenderHeight") _inputOverride = null;
        OnEditingSessionChanged();
        ScheduleSettingsSave();
        if (e.PropertyName == nameof(SettingsViewModel.PreviewTransport)) _ = UpdatePreviewTransportAsync();

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

    private WorkspaceConfiguration CreatePersistedSettings()
    {
        StoreCurrentProfilePaths(_currentDepthProfile);

        return new WorkspaceConfiguration { SessionPresetPath = Path.Combine(_workDir, "OfflinePreset.ini"),
            Editor = Settings.Editor, Preferences = Settings.Preferences };
    }

    private bool UseCpuPreview => string.Equals(Settings.PreviewTransport, "CPU", StringComparison.OrdinalIgnoreCase);

    private async Task ApplyGraphicsStateAsync(JsonElement info)
    {
        AdapterLuid = info.TryGetProperty("adapterLuid", out var luid) ? luid.GetUInt64() : 0;
        RenderWidth = info.GetProperty("width").GetUInt32(); RenderHeight = info.GetProperty("height").GetUInt32();
        var error = info.TryGetProperty("previewError", out var e) ? e.GetString() ?? "" : "";
        if (error.Length > 0 && error != _lastPreviewError) { _lastPreviewError = error; AppendLog(error); }
        if (error.Length > 0 && !IsGpuPreviewActive && info.TryGetProperty("cpuPreview", out var cpu) && !cpu.GetBoolean()) StatusText = "Preview unavailable";
        if (IsGpuPreviewActive && info.TryGetProperty("sharedPreviewHandle", out var handle) && handle.GetUInt64() == 0)
            await FallBackToCpuPreviewAsync(error.Length > 0 ? error : "Shared GPU preview is unavailable.");
    }

    public async Task FallBackToCpuPreviewAsync(string reason)
    {
        if (_isDisposed || _cpuPreviewFallback || UseCpuPreview) return;
        _cpuPreviewFallback = true;
        OnPropertyChanged(nameof(IsGpuPreviewActive));
        AppendLog(reason + " Switching to CPU preview; export dimensions are unchanged.");
        try {
            if (_session.IsConnected) await _session.CallAsync("set_preview_transport", new { cpu = true });
            if (!_isDisposed) StatusText = "CPU preview (GPU unavailable)";
        } catch (Exception ex) { if (!_isDisposed) { AppendLog(ex.Message); StatusText = "Preview unavailable"; } }
    }

    public void ReportPreviewError(string message) { if (!_isDisposed) AppendLog(message); }

    private async Task UpdatePreviewTransportAsync()
    {
        _cpuPreviewFallback = false;
        OnPropertyChanged(nameof(IsGpuPreviewActive));
        try {
            if (_session.IsConnected) {
                await _session.CallAsync("set_preview_transport", new { cpu = UseCpuPreview });
                await ApplyGraphicsStateAsync(await _session.CallAsync("get_runtime_info"));
            }
        } catch (Exception ex) { if (!_isDisposed) AppendLog(ex.Message); }
    }
    private string ActiveColorPath => IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.ColorPath : Settings.ColorPath;
    private string ActiveDepthPath => IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.DepthPath : Settings.DepthPath;
    private string ActiveDepthFormat => string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase)
        ? "raw"
        : IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.DepthFormat : Settings.DepthFormat;
    private string ActiveDepthProfile => EffectiveDepthProfile(ActiveDepthFormat);
    private string ActiveOutputPath => _inputOverride?.OutputPath ?? (IsGalleryMode && SelectedGalleryItem != null ? SelectedGalleryItem.OutputPath : Settings.OutputPath);

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
        var paths = new ProfilePaths
        {
            ColorPath = Settings.ColorPath,
            DepthPath = Settings.DepthPath,
            DepthFormat = Settings.DepthFormat,
            OutputPath = Settings.OutputPath,
            GalleryInputFolder = Settings.GalleryInputFolder,
            GalleryOutputFolder = Settings.GalleryOutputFolder
        };

        if (string.Equals(NormalizeDepthProfile(profile), "kk", StringComparison.OrdinalIgnoreCase))
            Settings.KkPaths = paths;
        else
            Settings.KksPaths = paths;
    }

    private ProfilePaths GetProfilePaths(string profile)
    {
        return string.Equals(NormalizeDepthProfile(profile), "kk", StringComparison.OrdinalIgnoreCase) ? Settings.KkPaths : Settings.KksPaths;
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

    private ProfilePaths CreateDefaultProfilePaths(string profile)
    {
        if (string.Equals(NormalizeDepthProfile(profile), "kk", StringComparison.OrdinalIgnoreCase))
        {
            const string kkExportDir = @"D:\Program Files\Koikatu\UserData\cap\OfflineReShade";
            return new ProfilePaths
            {
                ColorPath = Path.Combine(kkExportDir, "coloroutput.png"),
                DepthPath = Path.Combine(kkExportDir, "depthoutput.rfloat"),
                DepthFormat = "raw",
                OutputPath = Path.Combine(kkExportDir, "reshadeoutput.png"),
                GalleryInputFolder = string.Empty,
                GalleryOutputFolder = string.Empty
            };
        }

        return new ProfilePaths
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

    private static bool HasAnyProfilePath(ProfilePaths paths)
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
        async void Handle(object? _)
        {
            if (_isDisposed || _session.IsConnected) return;
            try { await StopPreviewAsync(false); StatusText = "Rendering process exited"; }
            catch (Exception ex) { AppendLog(ex.Message); }
        }
        if (_syncContext != null) _syncContext.Post(Handle, null); else Handle(null);
    }

    public async Task PrepareCloseAsync()
    {
        _automation.Cancel(restore: false);
        await _automation.StopAsync();
        if (_settingsStore.LoadError == null) await SaveSettingsNowAsync();
        else AppendLog(_settingsStore.LoadError);
        StopFpsPolling();
        await _session.DisposeAsync();
    }

    private void RaiseControlCommandStates()
    {
        ReShadeShotCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
        RaiseBatchApplyCanExecuteChanged();
    }

    private void RaiseBatchApplyCanExecuteChanged()
    {
        BatchApplySavePngCommand.RaiseCanExecuteChanged();
        BatchApplyReShadeShotCommand.RaiseCanExecuteChanged();
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
        _automation.Cancel(restore: false);
        _broker.Dispose();
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
        Settings.Session.Changed -= OnEditingSessionChanged;
        _session.OutputReceived -= OnPrototypeOutputReceived;
        _session.UnexpectedExit -= OnPreviewExited;
        _session.RuntimeResetting -= ResetPreviewPresentation;
        _session.PreviewPipeCreated -= _previewFrames.Start;
        StopFpsPolling();
        _previewFrames.Dispose();
    }
}
