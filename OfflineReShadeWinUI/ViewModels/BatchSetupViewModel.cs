using System.Collections.ObjectModel;
using System.Globalization;
using OfflineReShade.Core;
using OfflineReShade.WinUI.Mvvm;

namespace OfflineReShade.WinUI.ViewModels;

public sealed class BatchCandidateViewModel : ObservableObject
{
    private bool _selected;
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public RenderInput Input { get; }
    public string? Error { get; }
    public string Name => Path.GetFileName(Input.ColorPath);
    public string Description => Error ?? (Input.DepthPath.Length == 0 ? "Color only" : "Depth: " + Path.GetFileName(Input.DepthPath));
    public BatchCandidateViewModel(RenderInput input, string? error) { Input = input; Error = error; }
}
public sealed class BatchSetupViewModel : ObservableObject
{
    public ObservableCollection<BatchCandidateViewModel> Candidates { get; }
    private readonly RenderInput _expectedInput;
    private readonly long _revision;
    private string _folder, _suffix = "", _frames;
    private bool _selectedOnly, _shot = true, _resume = true;
    private int _policy = 1;
    public string Folder { get => _folder; set { if (SetProperty(ref _folder, value)) Review(); } }
    public string Suffix { get => _suffix; set { if (SetProperty(ref _suffix, value)) Review(); } }
    public string Frames { get => _frames; set { if (SetProperty(ref _frames, value)) Review(); } }
    public bool SelectedOnly { get => _selectedOnly; set { if (SetProperty(ref _selectedOnly, value)) Review(); } }
    public bool ReShadeShot { get => _shot; set { if (SetProperty(ref _shot, value)) Review(); } }
    public bool Resume { get => _resume; set { if (SetProperty(ref _resume, value)) Review(); } }
    public int PolicyIndex { get => _policy; set { if (SetProperty(ref _policy, value)) Review(); } }
    public string[] Policies { get; } = ["Replace existing", "Choose an unused name", "Fail if file exists", "Skip existing (unverified)"];
    public BatchRequest? Request { get; private set; }
    public bool CanStart => Request != null;
    public string Summary { get; private set; } = "";
    public string Details { get; private set; } = "";
    public BatchSetupViewModel(IEnumerable<BatchCandidateViewModel> candidates, RenderInput expected, long revision, string frames)
    {
        Candidates = new(candidates); _expectedInput = expected; _revision = revision; _frames = frames;
        _folder = Candidates.Count > 0 ? Path.GetDirectoryName(Candidates[0].Input.OutputPath)! : "";
        foreach (var item in Candidates) item.PropertyChanged += (_, _) => Review();
        Review();
    }
    private void Review()
    {
        Request = null;
        try
        {
            if (string.IsNullOrWhiteSpace(Folder)) throw new ArgumentException("Choose an output directory.");
            var folder = Path.GetFullPath(Folder);
            if (Suffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("The filename suffix contains an invalid character.");
            if (!int.TryParse(Frames, NumberStyles.None, CultureInfo.InvariantCulture, out var frames)) throw new ArgumentException("Warmup frames must be a whole number from 0 to 360000.");
            var candidates = Candidates.Where(x => !SelectedOnly || x.Selected).ToArray();
            var items = candidates.Select(x => x.Input with { OutputPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(x.Input.OutputPath) + Suffix + ".png") }).ToArray();
            var request = new BatchRequest(items, frames, ReShadeShot, Resume, ConflictPolicy: new[] { "replace", "rename", "fail", "skip" }[Math.Clamp(PolicyIndex, 0, 3)],
                ExpectedRevision: _revision, ExpectedInput: _expectedInput, InputErrors: candidates.Select(x => x.Error).ToArray());
            var check = AutomationEngine.PreflightBatch(request);
            Summary = $"{(SelectedOnly ? "Selected" : "All")}: {items.Length} images · {check.Valid} ready for render checks · {check.Invalid} invalid";
            Details = "Output: " + folder + "\nNaming: original output name + suffix + .png\n" +
                (items.FirstOrDefault() is { } first ? "Example: " + Path.GetFileName(first.OutputPath) + "\nDimensions: " + (first.Width == 0 ? "Each source image size" : $"{first.Width} × {first.Height}") + "\n" : "") +
                string.Join("\n", check.Items.Where(x => x.Error != null).Select(x => Path.GetFileName(x.Input.ColorPath) + ": " + x.Error)) +
                "\nInvalid items remain in the results. Compilation, textures and depth are checked during rendering. Current FX and output settings are fixed when the task starts.";
            if (check.Valid > 0) Request = request;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { Summary = error.Message; Details = ""; }
        OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Details));
    }
}
