using System.Collections.ObjectModel;
using OfflineReShade.Core;
using OfflineReShade.WinUI.Mvvm;

namespace OfflineReShade.WinUI.ViewModels;

public sealed partial class MainWindowViewModel
{
    public event Action? BatchRequested, BatchResultsRequested;
    public AsyncRelayCommand OpenBatchCommand { get; private set; } = null!;
    public AsyncRelayCommand ResumeSavedBatchCommand { get; private set; } = null!;
    public AsyncRelayCommand RetryFailedBatchCommand { get; private set; } = null!;
    public AsyncRelayCommand ResumeCurrentBatchCommand { get; private set; } = null!;
    public RelayCommand ShowBatchResultsCommand { get; private set; } = null!;
    public ObservableCollection<BatchResultViewModel> BatchRows { get; } = new();
    private BatchItemResult[]? _shownBatchItems;
    public string BatchManifestPath => _automation.Job?.ManifestPath ?? "";
    private void InitializeBatchCommands()
    {
        OpenBatchCommand = CreateCommand(() => { BatchRequested?.Invoke(); return Task.CompletedTask; }, () => CanEditSession && GalleryItems.Count > 0);
        ShowBatchResultsCommand = new(() => BatchResultsRequested?.Invoke(), () => HasBatchTask);
        ResumeSavedBatchCommand = CreateCommand(async () => {
            if (_picker == null || await _picker.PickBatchTaskAsync() is not { } path) return;
            await ExecuteOperationAsync("batch_resume", new { path }); BatchResultsRequested?.Invoke();
        }, () => CanEditSession);
        RetryFailedBatchCommand = CreateCommand(() => ExecuteOperationAsync("batch_retry", new { path = BatchManifestPath }), () => CanEditSession && _automation.Job?.Failed > 0);
        ResumeCurrentBatchCommand = CreateCommand(() => ExecuteOperationAsync("batch_resume", new { path = BatchManifestPath }), () => CanEditSession && BatchManifestPath.Length > 0);
    }
    public async Task<BatchSetupViewModel> PrepareBatchSetupAsync()
    {
        SubmitVisualEdits?.Invoke(); await _pendingEdits.FlushAsync();
        if (_session.IsConnected) await _automation.ExecuteAsync("get_edit_history", AutomationEngine.Json(new { }));
        return new(GalleryItems.Select(x => new BatchCandidateViewModel(InputForGallery(x), x.IsValid ? null : x.AmbiguousDepth
            ? "Choose a depth file before starting a new batch." : "Invalid gallery input; fix it before starting a new batch.")),
            _session.Current, _automation.History.Revision, Settings.GalleryBatchFrameDelay);
    }
    public Task StartReviewedBatchAsync(BatchRequest request) => ExecuteOperationAsync("batch_start", request);
    public Task<string?> PickBatchFolderAsync() => _picker?.PickFolderAsync() ?? Task.FromResult<string?>(null);
    private void UpdateBatchPresentation()
    {
        if (!ReferenceEquals(_shownBatchItems, _automation.Job?.Items))
        {
            _shownBatchItems = _automation.Job?.Items;
            var items = _shownBatchItems ?? [];
            while (BatchRows.Count > items.Length) BatchRows.RemoveAt(BatchRows.Count - 1);
            for (var i = 0; i < items.Length; i++)
            {
                if (i < BatchRows.Count && BatchRows[i].Result == items[i]) continue;
                var row = new BatchResultViewModel(items[i], error => ReportUiError(error, "locate_batch_output"));
                if (i < BatchRows.Count) BatchRows[i] = row; else BatchRows.Add(row);
            }
        }
        OnPropertyChanged(nameof(BatchManifestPath));
        OpenBatchCommand?.RaiseCanExecuteChanged(); ShowBatchResultsCommand?.RaiseCanExecuteChanged();
        ResumeSavedBatchCommand?.RaiseCanExecuteChanged(); RetryFailedBatchCommand?.RaiseCanExecuteChanged(); ResumeCurrentBatchCommand?.RaiseCanExecuteChanged();
    }
}
public sealed class BatchResultViewModel
{
    public BatchItemResult Result { get; }
    public string Name => Path.GetFileName(Result.Input.ColorPath);
    public string Status => Result.State;
    public string Details => string.Join("\n", new[] { Result.Error, Result.Warning, Result.OutputPath }.Where(x => !string.IsNullOrEmpty(x)));
    public AsyncRelayCommand LocateCommand { get; }
    public BatchResultViewModel(BatchItemResult result, Action<Exception> error)
    {
        Result = result;
        LocateCommand = new(async () => {
            var path = Result.OutputPath!;
            if (!File.Exists(path)) throw new FileNotFoundException("The result no longer exists.", path);
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select," + path); System.Diagnostics.Process.Start(start);
            await Task.CompletedTask;
        }, error, () => Result.OutputPath != null);
    }
}
