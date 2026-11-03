using System.Collections.ObjectModel;
using OfflineReShade.Core;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml.Media.Imaging;
using OfflineReShade.WinUI.Mvvm;
using OfflineReShade.WinUI.Services;

namespace OfflineReShade.WinUI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths _paths;
    private readonly NativeSession _session;
    private readonly UserSettingsStore _settingsStore;
    private readonly AutomationEngine _automation;
    private readonly AutomationBroker _broker;
    private readonly DeferredEditQueue _pendingEdits;
    public event Action? SubmitVisualEdits;
    private readonly string _workDir = Path.Combine(AutomationBroker.SessionDirectory, "work", Guid.NewGuid().ToString("N"));
    private bool _synchronizingInput;
    private bool _isInputPanelOpen;
    private string _inputSelectionMessage = "";
    private RenderInput? _runtimeInput => Settings.Session.Applied?.Input;
    private bool _cpuPreviewFallback;
    private string _lastPreviewError = "";
    public bool IsGpuPreviewActive => !UseCpuPreview && !_cpuPreviewFallback;
    public ulong AdapterLuid { get; private set; }
    public uint RenderWidth { get; private set; }
    public uint RenderHeight { get; private set; }
    public bool CanEditSession => !_automation.IsExclusive;
    public string AutomationSessionText => "MCP session: " + _broker.Descriptor.Id;
    public string AutomationSessionId => _broker.Descriptor.Id;
    public double BatchProgress => _automation.Job?.Completed ?? 0;
    public double BatchMaximum => Math.Max(1, _automation.Job?.Total ?? 1);
    public string BatchStatusText => _automation.Job is { } j ? $"{j.State}: {j.Completed}/{j.Total} · {j.Exported} exported · {j.Reused} reused · {j.Skipped} skipped · {j.Failed} failed" + (j.Error == null ? "" : " — " + j.Error) : "No batch task";
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
    private string _controlStatusText = "Disconnected";
    private string _previewInfoText = "Full-res ReShade preview";
    private string _fpsText = string.Empty;
    private string _logText = string.Empty;
    private string _addonDebugText = "No ImGui controls captured yet.";
    private string _addonSearchPath = string.Empty;
    private string _addonLogPath = string.Empty;
    private bool _allAddonsLoaded = true;
    private bool _isGalleryPanelOpen;
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

        _pendingEdits = new(error => ReportUiError(error));
        _automation = new AutomationEngine((m, a, t) => _session.CallAsync(m, a, t), OpenAutomationInputAsync, () => _session.Current,
            (path, token) => ApplySessionInputAsync(_session.Current with { PresetPath = path }, token, replacePreset: true), ApplyInputWatchAsync,
            () => { SubmitVisualEdits?.Invoke(); return _pendingEdits.FlushAsync(); }, () => HasPendingConfiguration ? CurrentRenderInput() : null, RebindPresetSourceAsync,
            _session, RestoreBatchPresentationAsync);
        _automation.Changed += OnAutomationChanged;
        _broker = new AutomationBroker(DispatchAutomationAsync, "Offline ReShade WinUI " + BuildContract.ProductVersion);

        StartPreviewCommand = CreateCommand(StartPreviewAsync, () => CanChooseSource);
        StopPreviewCommand = CreateCommand(StopPreviewAsync, () => IsPreviewRunning || _pendingOpens > 0 || _automation.WaitingInput != null);
        CancelBatchCommand = new RelayCommand(() => _automation.Cancel(), () => _automation.Job?.State == "running");
        OpenExportCommand = new RelayCommand(() => ExportRequested?.Invoke(), () => CanExport);
        PickExportFolderCommand = CreateCommand(PickExportFolderAsync, () => CanExport);
        OpenExportedImageCommand = CreateCommand(() => OpenExportedAsync(false), () => HasExportedImage);
        LocateExportedImageCommand = CreateCommand(() => OpenExportedAsync(true), () => HasExportedImage);
        ReloadCommand = CreateCommand(ReloadAsync, () => _session.IsConnected && CanEditSession);
        SavePresetCommand = CreateCommand(SavePresetAsync, () => _session.IsConnected && CanEditSession);
        SavePresetAsCommand = CreateCommand(SavePresetAsAsync, () => _session.IsConnected && CanEditSession);
        UndoCommand = CreateCommand(() => MoveHistoryAsync(false), () => _session.IsConnected && CanEditSession && _automation.History.CanUndo);
        RedoCommand = CreateCommand(() => MoveHistoryAsync(true), () => _session.IsConnected && CanEditSession && _automation.History.CanRedo);
        RestoreControlsCommand = CreateCommand(RestoreControlsAsync, () => _session.IsConnected && CanEditSession);
        ToggleInputModeCommand = CreateCommand(ToggleInputModeAsync, () => CanEditSession);
        ToggleGalleryPanelCommand = new RelayCommand(() => IsGalleryPanelOpen = !IsGalleryPanelOpen);
        RefreshGalleryCommand = new RelayCommand(RefreshGalleryItems, () => CanEditSession);
        InitializeBatchCommands();
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        OpenImageCommand = CreateCommand(OpenImageAsync, () => CanChooseSource);
        ConfigureListenerCommand = new RelayCommand(() => { IsSettingsOpen = false; IsInputPanelOpen = true; InputSelectionMessage = "Choose the game export folder, then start listening. Files may arrive later."; });
        StartListeningCommand = CreateCommand(StartListeningAsync, () => CanChooseSource);
        StopListeningCommand = CreateCommand(async () => await ExecuteOperationAsync("stop_listening"), () => CanChooseSource && IsListening);
        PickWatchFolderCommand = CreateCommand(PickWatchFolderAsync, () => CanChooseSource);
        OpenInputPanelCommand = new RelayCommand(() => { IsSettingsOpen = false; IsInputPanelOpen = !IsInputPanelOpen; });
        ClearDepthCommand = new RelayCommand(() => { Settings.DepthPath = ""; InputSelectionMessage = "Color-only input selected. Apply to preview when ready."; }, () => CanChooseSource);
        PickColorCommand = CreateCommand(PickInputColorAsync, () => CanChooseSource);
        PickDepthCommand = CreateCommand(PickInputDepthAsync, () => CanChooseSource);
        PickEffectDirCommand = CreateCommand(async () => await PickPathAsync(path => Settings.EffectDir = path, picker => picker.PickFolderAsync()));
        PickPresetCommand = CreateCommand(LoadPresetAsync, () => CanEditSession);
        PickGalleryInputFolderCommand = CreateCommand(async () => await PickPathAsync(path => Settings.GalleryInputFolder = path, picker => picker.PickFolderAsync()));
        PickGalleryOutputFolderCommand = CreateCommand(async () => await PickPathAsync(path => Settings.GalleryOutputFolder = path, picker => picker.PickFolderAsync()));

        CancelOperationCommand = CreateCommand(CancelUserOperationAsync, () => _automation.IsBusy);
        RetryOperationCommand = CreateCommand(async () => { await _automation.RetryAsync(); if (_session.IsConnected) await RefreshControlStateAsync(); }, () => CanRetryOperation && CanEditSession);
        LocateFeedbackCommand = CreateCommand(LocateFeedbackAsync, () => FeedbackPath.Length > 0);
        if (_settingsStore.LoadError != null) ReportUiError(new IOException(_settingsStore.LoadError), "load_settings");
        if (IsGalleryMode)
            RefreshGalleryItems();
    }

    public AsyncRelayCommand OpenImageCommand { get; }
    public AsyncRelayCommand StartListeningCommand { get; }
    public AsyncRelayCommand StopListeningCommand { get; }
    public AsyncRelayCommand PickWatchFolderCommand { get; }
    public RelayCommand ConfigureListenerCommand { get; }
    public RelayCommand OpenInputPanelCommand { get; }
    public RelayCommand ClearDepthCommand { get; }
    public bool CanChooseSource => !_automation.IsOperationExclusive;
    public bool IsInputPanelOpen { get => _isInputPanelOpen; set => SetProperty(ref _isInputPanelOpen, value); }
    public string InputSelectionMessage { get => _inputSelectionMessage; private set => SetProperty(ref _inputSelectionMessage, value); }
    public bool IsListening => _automation.WaitingInput != null || (_session.IsConnected && _session.Current.WatchInput);
    public string CurrentInputText => _runtimeInput?.ColorPath ?? "No image has been applied";
    public string CurrentDepthText => _runtimeInput is not { } input ? "No depth associated" : input.DepthPath.Length == 0 ? "Color only — no depth associated" : $"{input.DepthPath} ({input.DepthFormat}, {input.DepthProfile.ToUpperInvariant()})";
    public string CurrentPresetText => _runtimeInput is { PresetPath.Length: > 0 } input ? input.PresetPath : "Working preset — no source preset selected";
    public string InputSummary => _runtimeInput is not { } input ? "Input, depth and preset" :
        $"{Path.GetFileName(input.ColorPath)} · {(input.DepthPath.Length == 0 ? "Color only" : "Depth linked")} · {(input.PresetPath.Length == 0 ? "Working preset" : Path.GetFileName(input.PresetPath))}" +
        (IsListening ? " · Listening" : "") + (_automation.History.IsModified ? " · Unsaved FX changes" : "") +
        (HasPendingConfiguration ? " · Input changes pending" : "");
    public string InputSourceText => Activity.Runtime.WaitingForInput ? $"Waiting for game output: {Activity.Runtime.InputPath}"
        : IsListening ? "Game output — automatic updates are enabled" : "Files — changes are applied manually";

    private async Task OpenImageAsync()
    {
        if (_picker == null || await _picker.PickPngAsync() is not { } path) return;
        var pairing = InputPairing.Resolve(path, Settings.DepthProfile);
        var proposed = CurrentRenderInput() with { ColorPath = pairing.ColorPath, DepthPath = pairing.AutomaticDepth?.Path ?? "",
            DepthFormat = pairing.AutomaticDepth?.Format ?? "raw", WatchInput = false };
        if (pairing.IsAmbiguous)
        {
            SetInputDraft(proposed);
            InputSelectionMessage = "Multiple matching depth files were found. Choose one, or clear depth for color-only processing, then apply. The current preview is unchanged.";
            IsSettingsOpen = false; IsInputPanelOpen = true;
            return;
        }
        await ExecuteOperationAsync("open_input", proposed);
        InputSelectionMessage = proposed.DepthPath.Length == 0 ? "Opened without depth. You can associate a depth file below." : "Matching depth file associated.";
    }
    private async Task PickInputColorAsync()
    {
        if (_picker == null || await _picker.PickPngAsync() is not { } path) return;
        var pairing = InputPairing.Resolve(path, Settings.DepthProfile);
        Settings.InputMode = "Gallery"; Settings.ColorPath = path; Settings.DepthPath = pairing.AutomaticDepth?.Path ?? "";
        Settings.DepthFormat = pairing.AutomaticDepth?.Format ?? "raw";
        InputSelectionMessage = pairing.IsAmbiguous ? "Multiple depth matches. Choose a depth file explicitly before applying." : "Input selected. Apply to preview when ready.";
    }
    private async Task PickInputDepthAsync()
    {
        if (_picker == null || await _picker.PickDepthAsync() is not { } path) return;
        if (Settings.DepthProfile == "kk" && !Path.GetExtension(path).Equals(".rfloat", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("KK requires a raw .rfloat depth file. Select the KKS profile to use packed PNG depth.");
        Settings.DepthPath = path;
        Settings.DepthFormat = Path.GetExtension(path).Equals(".rfloat", StringComparison.OrdinalIgnoreCase) ? "raw" : "rgba";
        InputSelectionMessage = "Depth association changed. Apply to preview when ready.";
    }
    private async Task PickWatchFolderAsync()
    {
        if (_picker == null || await _picker.PickFolderAsync() is not { } folder) return;
        Settings.ColorPath = Path.Combine(folder, "coloroutput.png");
        Settings.DepthPath = Path.Combine(folder, Settings.DepthFormat == "rgba" ? "depthoutput.png" : "depthoutput.rfloat");
        InputSelectionMessage = "These game output paths may not exist yet. Check the encoding and start listening.";
    }
    private Task StartListeningAsync() => ExecuteOperationAsync("listen_input", CurrentRenderInput() with { WatchInput = true });
    private async Task LoadPresetAsync()
    {
        if (_picker == null || await _picker.PickIniAsync() is not { } path) return;
        if (_session.IsConnected) await ExecuteOperationAsync("set_preset", new { path });
        else Settings.PresetPath = path;
    }
    private void SetInputDraft(RenderInput input)
    {
        _synchronizingInput = true;
        try { Settings.ApplyInput(input); }
        finally { _synchronizingInput = false; }
        _currentDepthProfile = NormalizeDepthProfile(input.DepthProfile);
        _hasPersistedDepthProfile = true;
        StoreCurrentProfilePaths(_currentDepthProfile);
        OnPropertyChanged(nameof(InputMode)); OnPropertyChanged(nameof(IsGalleryMode)); OnPropertyChanged(nameof(InputModeButtonText));
        OnEditingSessionChanged(); ScheduleSettingsSave();
    }
    private async Task ApplyInputWatchAsync(bool enabled, CancellationToken token)
    {
        await _session.SetInputWatchAsync(enabled, token);
        Settings.InputMode = enabled ? "RealTime" : "Gallery";
        OnPropertyChanged(nameof(InputMode)); OnPropertyChanged(nameof(IsGalleryMode));
        OnEditingSessionChanged();
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
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand SavePresetCommand { get; }
    public AsyncRelayCommand ToggleInputModeCommand { get; }
    public RelayCommand ToggleGalleryPanelCommand { get; }
    public RelayCommand RefreshGalleryCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public AsyncRelayCommand PickColorCommand { get; }
    public AsyncRelayCommand PickDepthCommand { get; }
    public AsyncRelayCommand PickEffectDirCommand { get; }
    public AsyncRelayCommand PickPresetCommand { get; }
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
    private ActivitySnapshot Activity => _automation.Activity.Snapshot;
    public string StatusText => Activity.StatusText;
    public bool ShowEmptyCanvas => Activity.ShowEmpty;
    public string EmptyTitle => Activity.EmptyTitle;
    public string EmptyMessage => Activity.Runtime.InputPath.Length == 0 && !IsOperationRunning && Activity.Runtime.Error == null
        ? "Use Open image or Listen for game output in the toolbar above." : Activity.EmptyMessage;
    public bool HasBatchTask => _automation.Job != null;
    public bool IsOperationRunning => _automation.IsBusy;
    public bool FeedbackOpen { get => Activity.Notice != null || Activity.Runtime.Error != null || Activity.Runtime.PreviewWarning != null; set { if (!value) _automation.Activity.Dismiss(); } }
    public string FeedbackTitle => Activity.Notice is { } n ? $"{ApplicationActivity.Label(n.Operation)}: {n.Status}" : "Preview needs attention";
    public string FeedbackMessage => Activity.Notice?.Message ?? Activity.Runtime.Error ?? Activity.Runtime.PreviewWarning ?? "";
    public string FeedbackStatus => Activity.Notice?.Status ?? (Activity.Runtime.Error != null ? "failed" : "warning");
    public string FeedbackDetails => System.Text.Json.JsonSerializer.Serialize(Activity, AutomationEngine.JsonOptions) + Environment.NewLine + LogText;
    public bool CanRetryOperation => _automation.Activity.RetryRequest != null;
    public string FeedbackPath => Activity.Notice?.TargetPath ?? Activity.Notice?.SourcePath ?? Activity.Runtime.InputPath;
    public AsyncRelayCommand CancelOperationCommand { get; }
    public AsyncRelayCommand RetryOperationCommand { get; }
    public AsyncRelayCommand LocateFeedbackCommand { get; }
    private AsyncRelayCommand CreateCommand(Func<Task> action, Func<bool>? canExecute = null) => new(action, error => ReportUiError(error), canExecute);
    public void ReportUiError(Exception error, string operation = "edit")
    {
        AppendLog(error.ToString());
        _automation.Activity.Report(error, operation, _session.Current);
    }
    private async Task<JsonElement> ExecuteOperationAsync(string method, object? args = null, CancellationToken token = default)
    {
        await FinishUniformGestureAsync();
        var request = AutomationEngine.Json(args ?? new { });
        if (_automation.ReplacesPreset(method, request))
        {
            var decision = method == "export_image" ? _exportPresetDecision ?? throw new InvalidOperationException("Close and reopen Export image to review the changed preset.") : await ReviewUnsavedAsync();
            if (method == "export_image" && request.GetProperty("applyInput").GetProperty("presetPath").GetString() != _reviewedExportPreset)
                throw new InvalidOperationException("The pending preset changed. Close and reopen Export image to review it.");
            request = AutomationEngine.CopyPresetDecision(request, decision);
        }
        return await _automation.ExecuteAsync(method, request, token);
    }
    private Task LocateFeedbackAsync()
    {
        var path = Path.GetFullPath(FeedbackPath);
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
        if (directory == null) throw new DirectoryNotFoundException("The associated directory is unavailable.");
        var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(directory); System.Diagnostics.Process.Start(start);
        return Task.CompletedTask;
    }
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
                NotifyExportState();
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
            Settings.EffectsPaneOpen = settings.Preferences.EffectsPaneOpen;
            Settings.EffectsPaneWidth = settings.Preferences.EffectsPaneWidth;
            Settings.Theme = settings.Preferences.Theme;
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
        await ExecuteOperationAsync("set_effects_state", new { enabled });
        EffectsEnabled = enabled;
    }

    public async Task SetTechniqueStateAsync(TechniqueViewModel technique, bool enabled)
    {
        await ExecuteOperationAsync("set_technique_state", new { id = technique.Id, enabled });
        technique.IsEnabled = enabled;
        await RefreshControlStateAsync();
    }

    public async Task ReorderTechniquesAsync(IReadOnlyList<TechniqueViewModel> techniques)
    {
        if (!_session.IsConnected || techniques.Count == 0)
            return;

        var ids = new string[techniques.Count];
        for (var i = 0; i < techniques.Count; ++i)
            ids[i] = techniques[i].Id;

        try
        {
            await ExecuteOperationAsync("reorder_techniques", new { ids });
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

    private Task<JsonElement> SubmitEditAsync(string method, object args) => _automation.ExecuteSubmittedEditAsync(method, AutomationEngine.Json(args));
    public Task SetUniformAsync(UniformViewModel uniform, object value) =>
        _pendingEdits.SubmitNowAsync("uniform:" + uniform.Id, () => SubmitEditAsync("set_uniform", new { id = uniform.Id, value }));

    public void QueueUniformEdit(UniformViewModel uniform, object value)
    {
        var gesture = _uniformGesture;
        _pendingEdits.Enqueue("uniform:" + uniform.Id, async () => {
            var transactionId = gesture == null ? null : await gesture;
            await SubmitEditAsync("set_uniform", new { id = uniform.Id, value, transactionId });
        });
    }
    public void QueueAddonEdit(AddonImGuiControlViewModel control, string value) =>
        _pendingEdits.Enqueue("addon:" + control.Id, () => SubmitEditAsync("set_addon_imgui_value", new { id = control.Id, value }));

    public async Task SetPreprocessorDefinitionAsync(PreprocessorDefinitionViewModel definition, string value)
    {
        MarkEffectsLoading();
        await ExecuteOperationAsync("set_preprocessor_definition", new { effectName = definition.EffectName, name = definition.Name, value });
        definition.Value = value;
        await Task.Delay(500);
        await RefreshControlStateAsync();
    }

    public Task SetAddonImGuiValueAsync(AddonImGuiControlViewModel control, string value) =>
        _pendingEdits.SubmitNowAsync("addon:" + control.Id, () => SubmitEditAsync("set_addon_imgui_value", new { id = control.Id, value }));

    public async Task OpenNativeAddonPanelAsync()
    {
        await ExecuteOperationAsync("open_native_addon_panel");
    }

    public async Task SelectNativeAddonOverlayAsync(AddonOverlayViewModel? overlay)
    {
        // Presentation selection also runs while controls are rebuilt during an open.
        // User input into the overlay remains serialized and blocked during exclusive work.
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
            await SubmitEditAsync("send_addon_overlay_input", new
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
        if (item == null) return;
        if (item.AmbiguousDepth)
        {
            SetInputDraft(InputForGallery(item));
            InputSelectionMessage = "Multiple matching depth files. Choose one below, then apply; the current preview is unchanged.";
            IsInputPanelOpen = true; return;
        }
        await ExecuteOperationAsync("open_input", InputForGallery(item));
    }

    public async Task ToggleInputModeAsync()
    {
        if (IsListening) await ExecuteOperationAsync("stop_listening");
        else { IsSettingsOpen = false; IsInputPanelOpen = true; }
    }

    private Task StartPreviewAsync() => StartPreviewCoreAsync(false);
    private async Task StartPreviewCoreAsync(bool preserveBatch) => await ExecuteOperationAsync("open_input", CurrentRenderInput() with { WatchInput = false });
    private Task StopPreviewAsync() => StopPreviewAsync(updateStatus: true);
    private async Task StopPreviewAsync(bool updateStatus)
    {
        _pendingEdits.Clear();
        try { await _automation.StopSessionAsync(_session.StopAsync); }
        finally { await ResetLocalEditsAsync(); }
    }

    private void ResetPreviewPresentation()
    {
        PreviewOriginal = false;
        _runtimeGeneration++;
        _automation.Activity.Disconnected();
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

    private RenderInput CurrentRenderInput() => Settings.Editor.ToRenderInput(_paths.AppDirectory);
    private RenderInput InputForGallery(GalleryItemViewModel item) => CurrentRenderInput() with {
        ColorPath = item.ColorPath, DepthPath = item.DepthPath, DepthFormat = item.DepthFormat,
        DepthProfile = EffectiveDepthProfile(item.DepthFormat), OutputPath = item.OutputPath, WatchInput = false };

    private Task OpenAutomationInputAsync(RenderInput input, CancellationToken token) => ApplySessionInputAsync(input, token);
    private async Task RestoreBatchPresentationAsync()
    {
        PreviewInfoText = _session.IsConnected ? Path.GetFileName(_session.Current.ColorPath) : "No image opened";
        IsPreviewRunning = _session.IsConnected;
        if (!_session.IsConnected) return;
        await _session.CallAsync("set_preview_transport", new { cpu = UseCpuPreview });
        await RefreshControlStateAsync();
        StartFpsPolling();
    }
    private async Task ApplySessionInputAsync(RenderInput input, CancellationToken token, bool replacePreset = false)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _pendingOpens++;
        StopPreviewCommand.RaiseCanExecuteChanged();
        try
        {
            if (replacePreset) await _session.LoadPresetAsync(input.PresetPath, token);
            else await _session.OpenAsync(input, token);
            SetInputDraft(_session.Current);
            _pendingEdits.Clear();
            IsInputPanelOpen = false;
            InputSelectionMessage = "";
            PreviewInfoText = Path.GetFileName(_session.Current.ColorPath);
            var item = GalleryItems.FirstOrDefault(x => string.Equals(x.ColorPath, _session.Current.ColorPath, StringComparison.OrdinalIgnoreCase));
            SelectedGalleryItem = item;
        }
        finally
        {
            _pendingOpens--;
            StopPreviewCommand.RaiseCanExecuteChanged();
            PreviewInfoText = _session.IsConnected ? Path.GetFileName(_session.Current.ColorPath) : "No image opened";
            if (!_isDisposed && _session.IsConnected)
            {
                await _session.CallAsync("set_preview_transport", new { cpu = UseCpuPreview });
                IsPreviewRunning = true;
                ControlStatusText = "Connected";
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
                if (method == "set_preview_comparison") PreviewOriginal = result.GetProperty("previewOriginal").GetBoolean();
                if ((method is "cancel_operation" or "batch_cancel") && result.TryGetProperty("cancelling", out var cancelling) && cancelling.GetBoolean())
                {
                    await ResetLocalEditsAsync();
                    if (_session.IsConnected) await RefreshControlStateAsync();
                }
                if (method is "set_parameters" or "set_uniform" or "reset_uniform" or "set_technique_state" or "reorder_techniques" or "set_preset" or "set_effects_state" or "undo" or "redo" or "set_preprocessor_definition") await RefreshControlStateAsync();
                completion.TrySetResult(result);
            } catch (Exception ex) { completion.TrySetException(ex); }
        }
        if (_syncContext != null) _syncContext.Post(Execute, null); else Execute(null);
        return completion.Task;
    }

    private void OnAutomationChanged()
    {
        void Update()
        {
            if (_isDisposed) return;
            IsBatchApplying = _automation.Job?.State == "running";
            UpdateBatchPresentation();
            foreach (var property in new[] { nameof(CanChooseSource), nameof(IsListening), nameof(InputSourceText), nameof(CanEditSession), nameof(BatchProgress), nameof(BatchMaximum), nameof(BatchStatusText), nameof(HasBatchTask),
                nameof(StatusText), nameof(InputSummary), nameof(IsOperationRunning), nameof(ShowEmptyCanvas), nameof(EmptyTitle), nameof(EmptyMessage), nameof(FeedbackOpen), nameof(FeedbackTitle), nameof(FeedbackMessage),
                nameof(FeedbackStatus), nameof(FeedbackDetails), nameof(CanRetryOperation), nameof(FeedbackPath) }) OnPropertyChanged(property);
            OpenImageCommand.RaiseCanExecuteChanged(); StartListeningCommand.RaiseCanExecuteChanged(); StopListeningCommand.RaiseCanExecuteChanged();
            PickWatchFolderCommand.RaiseCanExecuteChanged(); PickColorCommand.RaiseCanExecuteChanged(); PickDepthCommand.RaiseCanExecuteChanged(); PickPresetCommand.RaiseCanExecuteChanged();
            ClearDepthCommand.RaiseCanExecuteChanged(); StopPreviewCommand.RaiseCanExecuteChanged();
            CancelBatchCommand.RaiseCanExecuteChanged(); CancelOperationCommand.RaiseCanExecuteChanged();
            RetryOperationCommand.RaiseCanExecuteChanged(); LocateFeedbackCommand.RaiseCanExecuteChanged();
            StartPreviewCommand.RaiseCanExecuteChanged(); NotifyExportState();
            NotifyHistoryState();
            ToggleInputModeCommand.RaiseCanExecuteChanged(); RefreshGalleryCommand.RaiseCanExecuteChanged(); RaiseControlCommandStates();
        }
        if (_syncContext != null && SynchronizationContext.Current != _syncContext) _syncContext.Post(_ => Update(), null); else Update();
    }

    private async Task ReloadAsync()
    {
        MarkEffectsLoading();
        await ExecuteOperationAsync("reload_effects");
        await Task.Delay(500);
        await RefreshControlStateAsync();
    }

    private async Task SavePresetAsync()
    {
        if (string.IsNullOrWhiteSpace(_session.Current.PresetPath)) { await SavePresetAsAsync(); return; }
        await ExecuteOperationAsync("save_preset");
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
            _automation.ObserveRuntime(runtimeInfo);
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
                    _automation.ObserveRuntime(info);
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
                ReportUiError(ex, "recover_session");
                await StopPreviewAsync(updateStatus: false);
                return;
            }
            catch (Exception ex)
            {
                if (_isDisposed || cancellationToken.IsCancellationRequested) return;
                FpsText = "FPS unavailable";
                ReportUiError(ex, "observe_runtime");
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
        NotifyExportState();
        OnPropertyChanged(nameof(CurrentInputText)); OnPropertyChanged(nameof(CurrentDepthText)); OnPropertyChanged(nameof(CurrentPresetText));
        OnPropertyChanged(nameof(InputSummary));
        OnPropertyChanged(nameof(InputSourceText)); OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(HasPendingConfiguration));
        OnPropertyChanged(nameof(ConfigurationStatusText));
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_synchronizingInput) return;
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
            }

            if (string.Equals(Settings.DepthProfile, "kk", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Settings.DepthFormat, "raw", StringComparison.OrdinalIgnoreCase))
            {
                Settings.DepthFormat = "raw";
                if (Path.GetExtension(Settings.DepthPath).Equals(".png", StringComparison.OrdinalIgnoreCase)) Settings.DepthPath = "";
                InputSelectionMessage = "KK requires raw .rfloat depth. Associate the correct depth file before applying.";
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

        if (!_isRestoringSettings && IsProfilePathProperty(e.PropertyName))
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
            ReportUiError(ex, "save_settings");
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
        PreviewOriginal = info.TryGetProperty("previewOriginal", out var original) && original.GetBoolean();
        AdapterLuid = info.TryGetProperty("adapterLuid", out var luid) ? luid.GetUInt64() : 0;
        RenderWidth = info.GetProperty("width").GetUInt32(); RenderHeight = info.GetProperty("height").GetUInt32();
        OnPropertyChanged(nameof(ExportDimensionsText));
        var error = info.TryGetProperty("previewError", out var e) ? e.GetString() ?? "" : "";
        if (error.Length > 0 && error != _lastPreviewError) { _lastPreviewError = error; AppendLog(error); }
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
        } catch (Exception ex) { if (!_isDisposed) ReportUiError(ex, "preview_transport"); }
    }

    public void ReportPreviewError(string message) { if (!_isDisposed) ReportUiError(new IOException(message), "preview_transport"); }

    private async Task UpdatePreviewTransportAsync()
    {
        _cpuPreviewFallback = false;
        OnPropertyChanged(nameof(IsGpuPreviewActive));
        try {
            if (_session.IsConnected) {
                await _session.CallAsync("set_preview_transport", new { cpu = UseCpuPreview });
                await ApplyGraphicsStateAsync(await _session.CallAsync("get_runtime_info"));
            }
        } catch (Exception ex) { if (!_isDisposed) ReportUiError(ex, "preview_transport"); }
    }

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

    private void OnPrototypeOutputReceived(string? message)
    {
        AppendLog(message);
        if (message?.StartsWith("DEPTH_PROFILE_FALLBACK=KKS", StringComparison.OrdinalIgnoreCase) == true)
            Settings.DepthProfile = "kks";
    }

    private void RefreshGalleryItems()
    {
        var selectedPath = _runtimeInput?.ColorPath;
        GalleryItems.Clear();
        OpenBatchCommand?.RaiseCanExecuteChanged();
        var folder = Settings.GalleryInputFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) { SelectedGalleryItem = null; return; }
        var output = string.IsNullOrWhiteSpace(Settings.GalleryOutputFolder) ? folder : Settings.GalleryOutputFolder;
        foreach (var color in Directory.EnumerateFiles(folder).Where(InputPairing.IsColorFile).Order(StringComparer.OrdinalIgnoreCase))
        {
            var pairing = InputPairing.Resolve(color, Settings.DepthProfile);
            GalleryItems.Add(new(pairing.Name, color, pairing.AutomaticDepth?.Path ?? "", pairing.AutomaticDepth?.Format ?? "raw",
                Path.Combine(output, pairing.OutputName), !pairing.IsAmbiguous, pairing.IsAmbiguous));
        }
        SelectedGalleryItem = GalleryItems.FirstOrDefault(x => x.ColorPath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        OpenBatchCommand?.RaiseCanExecuteChanged();
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

    private void RefreshOutputInfo() => PreviewInfoText = _runtimeInput == null ? "No image opened" : Path.GetFileName(_runtimeInput.ColorPath);

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
            try { await StopPreviewAsync(false); ReportUiError(new IOException("The rendering process exited. Open the input again to restore the session."), "runtime_exit"); }
            catch (Exception ex) { ReportUiError(ex, "stop_session"); }
        }
        if (_syncContext != null) _syncContext.Post(Handle, null); else Handle(null);
    }

    public async Task PrepareCloseAsync()
    {
        await FinishUniformGestureAsync();
        if (_automation.IsOperationExclusive) throw new InvalidOperationException("Finish or cancel the current operation before closing.");
        var decision = await ReviewUnsavedAsync();
        _automation.Cancel(restore: false);
        await _automation.StopSessionAsync(async () => {
            if (_settingsStore.LoadError == null) await SaveSettingsNowAsync();
            else AppendLog(_settingsStore.LoadError);
            StopFpsPolling();
            await _session.DisposeAsync();
        }, decision);
    }

    private void RaiseControlCommandStates()
    {
        NotifyExportState();
        ReloadCommand.RaiseCanExecuteChanged();
        SavePresetCommand.RaiseCanExecuteChanged();
        SavePresetAsCommand?.RaiseCanExecuteChanged(); UndoCommand?.RaiseCanExecuteChanged(); RedoCommand?.RaiseCanExecuteChanged(); RestoreControlsCommand?.RaiseCanExecuteChanged();
        RaiseBatchApplyCanExecuteChanged();
    }

    private void RaiseBatchApplyCanExecuteChanged()
    {
        OpenBatchCommand?.RaiseCanExecuteChanged();
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
        _pendingEdits.Dispose();
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
