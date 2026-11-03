using System.Text.Json;
using OfflineReShade.Core;
using OfflineReShade.WinUI.Mvvm;

namespace OfflineReShade.WinUI.ViewModels;

public sealed partial class MainWindowViewModel
{
    public AsyncRelayCommand UndoCommand { get; }
    public AsyncRelayCommand RedoCommand { get; }
    public AsyncRelayCommand SavePresetAsCommand { get; }
    public AsyncRelayCommand RestoreControlsCommand { get; }
    public Func<string, bool, Task<string>>? UnsavedDecisionRequested { get; set; }
    public string PresetEditStatus => _automation.History.IsModified ? "Preset has unsaved FX changes" : "No unsaved FX changes";
    public string HistoryWarning => _automation.History.Warning ?? "";
    public string AddonHistoryCapability => _automation.History.AddonCapability;
    private Task<string>? _uniformGesture;
    private Task _endingGesture = Task.CompletedTask;
    private JsonElement? _exportPresetDecision;
    private string? _reviewedExportPreset;
    public async Task PrepareExportReviewAsync()
    {
        await FinishUniformGestureAsync();
        _exportPresetDecision = null; _reviewedExportPreset = null;
        if (!HasPendingConfiguration) return;
        var input = CurrentRenderInput();
        if (!_automation.ReplacesPreset("open_input", AutomationEngine.Json(input))) return;
        _exportPresetDecision = await ReviewUnsavedAsync(); _reviewedExportPreset = input.PresetPath;
    }
    private void NotifyHistoryState()
    {
        OnPropertyChanged(nameof(FooterEditStatus)); OnPropertyChanged(nameof(PresetEditStatus)); OnPropertyChanged(nameof(HistoryWarning));
        OnPropertyChanged(nameof(InputSummary)); OnPropertyChanged(nameof(IsPresetModified));
        UndoCommand?.RaiseCanExecuteChanged(); RedoCommand?.RaiseCanExecuteChanged(); SavePresetAsCommand?.RaiseCanExecuteChanged();
    }
    public void BeginUniformGesture()
    {
        if (_uniformGesture != null) return;
        _uniformGesture = BeginAsync();
        async Task<string> BeginAsync()
        {
            await _endingGesture;
            var result = await SubmitEditAsync("begin_edit", new { });
            return result.GetProperty("transactionId").GetString()!;
        }
    }
    public Task FinishUniformGestureAsync()
    {
        if (_uniformGesture == null) return _endingGesture;
        var gesture = _uniformGesture; _uniformGesture = null;
        return _endingGesture = EndAsync();
        async Task EndAsync()
        {
            var id = await gesture;
            try { await _pendingEdits.FlushAsync(); }
            finally
            {
                if (_automation.History.TransactionId == id) await SubmitEditAsync("end_edit", new { transactionId = id });
            }
        }
    }
    public async Task ResetUniformAsync(UniformViewModel uniform)
    {
        await FinishUniformGestureAsync();
        await _pendingEdits.SubmitNowAsync("uniform:" + uniform.Id, () => SubmitEditAsync("reset_uniform", new { id = uniform.Id }));
        await RefreshControlStateAsync();
    }
    public Task RejectUniformEditAsync(UniformViewModel uniform) => _pendingEdits.SubmitNowAsync("uniform:" + uniform.Id,
        () => Task.FromException(new ArgumentException("Enter finite numeric values for " + uniform.Label + ", or restore controls from the renderer.")));
    private async Task MoveHistoryAsync(bool redo)
    {
        await ExecuteOperationAsync(redo ? "redo" : "undo");
        await RefreshControlStateAsync();
    }
    private async Task RestoreControlsAsync()
    {
        try { await FinishUniformGestureAsync(); await _pendingEdits.FlushAsync(); }
        catch (Exception error) { ReportUiError(error); }
        _endingGesture = Task.CompletedTask;
        _pendingEdits.Clear();
        await RefreshControlStateAsync();
    }
    private async Task ResetLocalEditsAsync()
    {
        // Native/coordinator cancellation has already drained before dropping the UI barrier.
        var gesture = _uniformGesture;
        _uniformGesture = null;
        string? id = null;
        try { if (gesture != null) id = await gesture; await _endingGesture; } catch (Exception) { /* Reported by the operation/queue owner. */ }
        if (id != null && _automation.History.TransactionId == id) await SubmitEditAsync("end_edit", new { transactionId = id });
        _endingGesture = Task.CompletedTask;
        _pendingEdits.Clear();
    }
    private async Task CancelUserOperationAsync()
    {
        await _automation.ExecuteAsync("cancel_operation", AutomationEngine.Json(new { }));
        await ResetLocalEditsAsync();
        if (_session.IsConnected) await RefreshControlStateAsync();
    }
    private async Task RebindPresetSourceAsync(string path, CancellationToken token)
    {
        await _session.SetPresetSourceAsync(path, token);
        Settings.PresetPath = path; // Preserve unrelated pending fields.
        OnPropertyChanged(nameof(CurrentPresetText)); OnPropertyChanged(nameof(PresetTitle));
    }
    private async Task SavePresetAsAsync()
    {
        if (_picker == null || await _picker.PickPresetSaveAsync() is not { } path) return;
        await ExecuteOperationAsync("save_preset_as", new { path });
    }
    private async Task<JsonElement> ReviewUnsavedAsync()
    {
        SubmitVisualEdits?.Invoke();
        await _pendingEdits.FlushAsync();
        Exception? readError = null;
        if (_session.IsConnected)
        {
            try { await _automation.ExecuteAsync("get_edit_history", AutomationEngine.Json(new { })); }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException) { readError = error; }
        }
        var state = _automation.History;
        if (!state.IsModified && readError == null) return AutomationEngine.Json(new { unsavedChanges = _session.IsConnected ? "reject" : "discard", expectedRevision = state.Revision });
        var canSave = _session.IsConnected && state.Available && readError == null;
        var message = CurrentPresetText + (canSave ? "" : "\nSaving is unavailable until the renderer and FX state are recovered.\n" + (readError?.Message ?? state.Warning));
        var choice = UnsavedDecisionRequested == null ? "cancel" : await UnsavedDecisionRequested(message, canSave);
        if (choice == "cancel") throw new OperationCanceledException("Kept the current editing session.");
        if (choice == "save" && !canSave) throw new InvalidOperationException("Recover the renderer before saving this preset.");
        string? savePath = null;
        if (choice == "save" && string.IsNullOrWhiteSpace(_session.Current.PresetPath))
        {
            savePath = _picker == null ? null : await _picker.PickPresetSaveAsync();
            if (savePath == null) throw new OperationCanceledException("Preset save was cancelled.");
        }
        return AutomationEngine.Json(new { unsavedChanges = choice, savePath, expectedRevision = state.Revision });
    }
}
