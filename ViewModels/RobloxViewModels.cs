using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

/// <summary>Where Roblox is, shared by the cursor and font pages.</summary>
public sealed class RobloxLocationViewModel : ObservableObject
{
    private string _found = "Looking for Roblox…";
    private bool _ok;
    private string _message = "";
    public RobloxSettings Settings => Svc.S.Roblox;
    public string Found { get => _found; private set => Set(ref _found, value); }
    public bool IsFound { get => _ok; private set => Set(ref _ok, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
    public RelayCommand FindAgainCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public event Action? Changed;

    public RobloxLocationViewModel()
    {
        FindAgainCommand = new RelayCommand(Refresh);
        ChooseFolderCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your Roblox folder (the one with RobloxPlayerBeta.exe, or a Bloxstrap-style folder)" };
            if (dlg.ShowDialog() != true) return;
            if (Svc.RobloxAssets.FromFolder(dlg.FolderName) == null) { Message = "No Roblox in that folder. Pick the folder with RobloxPlayerBeta.exe."; return; }
            Settings.CustomFolder = dlg.FolderName;
            Refresh();
        });
        Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(RobloxSettings.KeepAfterUpdates)) OnPropertyChanged(nameof(KeepAfterUpdates)); };
        Refresh();
    }

    public bool KeepAfterUpdates { get => Settings.KeepAfterUpdates; set { Settings.KeepAfterUpdates = value; OnPropertyChanged(); } }

    public void Refresh()
    {
        var installs = Svc.RobloxAssets.FindInstalls();
        IsFound = installs.Count > 0;
        Found = installs.Count == 0
            ? "Roblox wasn't found. Install it, or use Choose folder to show Nighty where it is."
            : "Found: " + string.Join(", ", installs.Select(i => i.Display)) + (Svc.Roblox.IsRunning ? " (Roblox is running: restart it to see changes)" : "");
        Message = "";
        Changed?.Invoke();
    }
}

public sealed class CursorItemViewModel : ObservableObject
{
    private BitmapSource? _thumb;
    private bool _applied;
    public required CursorSpec Spec { get; init; }
    public string Name => Spec.Name;
    public string Description => Spec.IsBuiltIn ? Spec.Description : (Spec.Kind == CursorKind.Image ? "Made from a picture" : "Made in the Cursor Builder");
    public bool IsBuiltIn => Spec.IsBuiltIn;
    public BitmapSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }
    public bool IsApplied { get => _applied; set => Set(ref _applied, value); }
}

public sealed class CursorsViewModel : ObservableObject
{
    private readonly RobloxLocationViewModel _loc;
    private CursorItemViewModel? _selected;
    private string _message = "";
    private bool _busy;
    private BitmapSource? _preview, _previewReal;
    private readonly DispatcherTimer _rerender = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public ObservableCollection<CursorItemViewModel> Items { get; } = new();
    public RobloxSettings Settings => Svc.S.Roblox;
    public CursorItemViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanDelete)); OnPropertyChanged(nameof(SelectedName)); OnPropertyChanged(nameof(SelectedDescription)); RenderPreview(); } }
    }
    public bool HasSelection => Selected != null;
    public bool CanDelete => Selected is { IsBuiltIn: false };
    public string SelectedName => Selected?.Name ?? "No cursor selected";
    public string SelectedDescription => Selected?.Description ?? "";
    public BitmapSource? Preview { get => _preview; private set => Set(ref _preview, value); }
    public BitmapSource? PreviewReal { get => _previewReal; private set => Set(ref _previewReal, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); UseCommand.Refresh(); RestoreCommand.Refresh(); } }

    public string[] Sizes { get; } = { "small", "medium", "large" };
    public string CursorSize { get => Settings.CursorSize; set { Settings.CursorSize = value; OnPropertyChanged(); } }
    public double Brightness { get => Settings.CursorBrightness; set { Settings.CursorBrightness = value; OnPropertyChanged(); OnPropertyChanged(nameof(BrightnessText)); _rerender.Stop(); _rerender.Start(); } }
    public string BrightnessText => Settings.CursorBrightness == 0 ? "Normal" : $"{Settings.CursorBrightness:+0;-0} %";
    public bool FirstPerson { get => Settings.FirstPerson; set { Settings.FirstPerson = value; OnPropertyChanged(); } }

    public int[] ExportSizes { get; } = { 32, 64, 128, 256 };
    private int _exportSize = 64;
    public int ExportSize { get => _exportSize; set => Set(ref _exportSize, value); }

    public AsyncCommand UseCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand OpenExportFolderCommand { get; }
    public RelayCommand EditCopyCommand { get; }
    public event Action<CursorSpec>? EditRequested;

    public CursorsViewModel(RobloxLocationViewModel loc)
    {
        _loc = loc;
        _rerender.Tick += (_, _) => { _rerender.Stop(); RenderAll(); };
        UseCommand = new AsyncCommand(UseInRoblox, () => !IsBusy && Selected != null);
        RestoreCommand = new AsyncCommand(RestoreRoblox, () => !IsBusy);
        DeleteCommand = new RelayCommand(Delete, () => CanDelete);
        ExportCommand = new RelayCommand(Export, () => Selected != null);
        OpenExportFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(ExportFolder); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExportFolder}\"") { UseShellExecute = true }); });
        EditCopyCommand = new RelayCommand(() => { if (Selected != null && Selected.Spec.Kind == CursorKind.Builder) EditRequested?.Invoke(Selected.Spec); else Message = "Only cursors made in the builder can be edited. Make a copy from a picture instead."; });
        Reload();
    }

    public static string ExportFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Nighty", "Cursors");

    public void Reload(string? select = null)
    {
        var keep = select ?? Selected?.Spec.Id ?? Settings.CursorId;
        Items.Clear();
        foreach (var s in CursorLibrary.All()) Items.Add(new CursorItemViewModel { Spec = s, IsApplied = s.Id == Settings.CursorId });
        RenderAll();
        Selected = Items.FirstOrDefault(i => i.Spec.Id == keep) ?? Items.FirstOrDefault();
    }

    private void RenderAll()
    {
        foreach (var i in Items) i.Thumb = CursorRenderer.Render(i.Spec, 48, Settings.CursorBrightness, checker: false);
        RenderPreview();
    }

    private void RenderPreview()
    {
        if (Selected == null) { Preview = PreviewReal = null; return; }
        Preview = CursorRenderer.Render(Selected.Spec, 160, Settings.CursorBrightness, checker: true);
        PreviewReal = CursorRenderer.Render(Selected.Spec, 64, Settings.CursorBrightness, checker: false);
    }

    private async Task UseInRoblox()
    {
        if (Selected == null) return;
        IsBusy = true; Message = "Adding to Roblox…";
        try
        {
            var spec = Selected.Spec; var fp = Settings.FirstPerson;
            // Draw on the UI thread, write the files on a worker.
            var png = CursorRenderer.ToPng(CursorRenderer.Render(spec, RobloxAssetsService.SizeToPx(Settings.CursorSize), Settings.CursorBrightness));
            var r = await Task.Run(() => Svc.RobloxAssets.ApplyCursorPng(png, fp));
            if (r.Changed > 0)
            {
                Settings.CursorId = spec.Id;
                foreach (var i in Items) i.IsApplied = i.Spec.Id == spec.Id;
                Message = $"“{spec.Name}” added to Roblox. " + (r.Note ?? "") + (r.Error != null ? " But one place failed: " + r.Error : "");
            }
            else Message = r.Error ?? "Nothing was changed.";
        }
        finally { IsBusy = false; _loc.Refresh(); }
    }

    private async Task RestoreRoblox()
    {
        IsBusy = true;
        try
        {
            var r = await Task.Run(Svc.RobloxAssets.RestoreCursor);
            if (r.Error != null) Message = r.Error;
            else { Settings.CursorId = ""; foreach (var i in Items) i.IsApplied = false; Message = "Roblox's own cursor is back."; }
        }
        finally { IsBusy = false; }
    }

    private void Delete()
    {
        if (Selected is not { IsBuiltIn: false } s) return;
        if (!Dialogs.Confirm("Delete this cursor?", $"“{s.Name}” will be removed from your cursors.", "Delete", danger: true)) return;
        if (Settings.CursorId == s.Spec.Id) Message = "It's still used in Roblox: press Restore to bring Roblox's own back.";
        CursorLibrary.Delete(s.Spec);
        Reload();
        if (Message.Length == 0) Message = "Cursor deleted.";
    }

    private void Export()
    {
        if (Selected == null) return;
        try
        {
            Directory.CreateDirectory(ExportFolder);
            var safe = string.Concat(Selected.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            var png = CursorRenderer.ToPng(CursorRenderer.Render(Selected.Spec, ExportSize, Settings.CursorBrightness));
            File.WriteAllBytes(Path.Combine(ExportFolder, $"{safe} {ExportSize}.png"), png);
            var set = new Dictionary<int, byte[]>();
            foreach (var s in new[] { 32, 48, 64, 128, 256 }) set[s] = CursorRenderer.ToPng(CursorRenderer.Render(Selected.Spec, s, Settings.CursorBrightness));
            File.WriteAllBytes(Path.Combine(ExportFolder, safe + ".cur"), CursorRenderer.ToCur(set, Selected.Spec.CenterHotspot));
            Message = "Exported to " + ExportFolder;
        }
        catch (Exception ex) { Message = "Export failed: " + ex.Message; }
    }
}

// ============================================================ builder

public sealed class ShapeChoice { public required CursorShape Shape { get; init; } public required string Label { get; init; } public override string ToString() => Label; }

public sealed class BuilderViewModel : ObservableObject
{
    private CursorSpec _spec = NewSpec();
    private CursorLayer? _layer;
    private BitmapSource? _preview, _previewReal;
    private string _name = "My cursor", _message = "";
    private string? _editingId;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public ObservableCollection<CursorLayer> Layers { get; } = new();
    public List<ShapeChoice> Shapes { get; } = new()
    {
        new() { Shape = CursorShape.Plus, Label = "Plus" }, new() { Shape = CursorShape.Split, Label = "Split crosshair" }, new() { Shape = CursorShape.X, Label = "X" },
        new() { Shape = CursorShape.Dot, Label = "Dot" }, new() { Shape = CursorShape.Ring, Label = "Ring" }, new() { Shape = CursorShape.Square, Label = "Square" },
        new() { Shape = CursorShape.Diamond, Label = "Diamond" }, new() { Shape = CursorShape.Triangle, Label = "Triangle" }, new() { Shape = CursorShape.Star, Label = "Star" },
        new() { Shape = CursorShape.Arrow, Label = "Arrow" }, new() { Shape = CursorShape.Line, Label = "Line" },
    };
    public string[] ColorPresets { get; } = { "#FFFFFF", "#22E27A", "#EF4444", "#F59E0B", "#FACC15", "#22D3EE", "#3B82F6", "#8B5CF6", "#EC4899", "#000000" };
    public ShapeChoice? SelectedShape
    {
        get => _layer == null ? null : Shapes.FirstOrDefault(s => s.Shape == _layer.Shape);
        set { if (_layer != null && value != null) { _layer.Shape = value.Shape; OnPropertyChanged(); OnPropertyChanged(nameof(ShowGap)); OnPropertyChanged(nameof(ShowFilled)); OnPropertyChanged(nameof(ShowThickness)); } }
    }
    public bool ShowGap => _layer is { Shape: CursorShape.Plus or CursorShape.Split or CursorShape.X };
    public bool ShowFilled => _layer is { Shape: CursorShape.Square or CursorShape.Diamond or CursorShape.Triangle or CursorShape.Star };
    public bool ShowThickness => _layer is { Shape: not (CursorShape.Dot or CursorShape.Arrow) };

    public CursorLayer? SelectedLayer
    {
        get => _layer;
        set
        {
            if (!Set(ref _layer, value)) return;
            OnPropertyChanged(nameof(HasLayer)); OnPropertyChanged(nameof(SelectedShape)); OnPropertyChanged(nameof(ShowGap)); OnPropertyChanged(nameof(ShowFilled)); OnPropertyChanged(nameof(ShowThickness));
        }
    }
    public bool HasLayer => _layer != null;
    public string Name { get => _name; set => Set(ref _name, value); }
    public BitmapSource? Preview { get => _preview; private set => Set(ref _preview, value); }
    public BitmapSource? PreviewReal { get => _previewReal; private set => Set(ref _previewReal, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Heading => _editingId == null ? "New cursor" : "Editing a saved cursor";
    public string LayersText => $"{Layers.Count} layer{(Layers.Count == 1 ? "" : "s")}";
    public bool CanSaveAsNew => _editingId != null;

    public RelayCommand AddLayerCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand UpCommand { get; }
    public RelayCommand DownCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsNewCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand SetColorCommand { get; }
    public event Action<string>? Saved;

    private static CursorSpec NewSpec() => new() { Name = "My cursor" };

    public BuilderViewModel()
    {
        _timer.Tick += (_, _) => { _timer.Stop(); RenderNow(); };
        AddLayerCommand = new RelayCommand(p =>
        {
            if (Layers.Count >= 8) { Message = "A cursor can have up to 8 layers. Delete one to add another."; return; }
            var shape = Enum.TryParse<CursorShape>(p?.ToString(), out var s) ? s : CursorShape.Plus;
            var l = new CursorLayer { Shape = shape, Size = shape is CursorShape.Dot ? 10 : shape is CursorShape.Arrow ? 44 : 36 };
            Add(l); SelectedLayer = l; Message = "";
        });
        DuplicateCommand = new RelayCommand(() => { if (_layer != null && Layers.Count < 8) { var c = _layer.Clone(); Add(c); SelectedLayer = c; } });
        RemoveCommand = new RelayCommand(() =>
        {
            if (_layer == null) return;
            int i = Layers.IndexOf(_layer); Layers.Remove(_layer);
            SelectedLayer = Layers.Count == 0 ? null : Layers[Math.Min(i, Layers.Count - 1)];
            Changed();
        });
        UpCommand = new RelayCommand(() => Move(+1));
        DownCommand = new RelayCommand(() => Move(-1));
        NewCommand = new RelayCommand(() => Load(NewSpec(), null));
        SetColorCommand = new RelayCommand(p => { if (_layer != null && p is string hex) _layer.Color = hex; });
        SaveCommand = new RelayCommand(() => Save(asNew: false));
        SaveAsNewCommand = new RelayCommand(() => Save(asNew: true), () => CanSaveAsNew);
        Layers.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(LayersText)); Changed(); };
        Load(new CursorSpec { Name = "My cursor", Layers = { new CursorLayer { Shape = CursorShape.Plus, Size = 38, Thickness = 4, Color = "#FFFFFF" } } }, null);
    }

    private void Add(CursorLayer l) { l.PropertyChanged += (_, _) => Changed(); Layers.Add(l); }

    private void Move(int dir)
    {
        if (_layer == null) return;
        int i = Layers.IndexOf(_layer), j = i + dir;
        if (j < 0 || j >= Layers.Count) return;
        Layers.Move(i, j);
    }

    /// <summary>Loads a cursor into the editor. <paramref name="editId"/> is set when it is a saved cursor being edited.</summary>
    public void Load(CursorSpec spec, string? editId)
    {
        _editingId = editId;
        Layers.Clear();
        foreach (var l in spec.Layers) Add(l.Clone());
        Name = editId != null ? spec.Name : spec.Name;
        _spec = spec;
        SelectedLayer = Layers.FirstOrDefault();
        OnPropertyChanged(nameof(Heading)); OnPropertyChanged(nameof(CanSaveAsNew));
        SaveAsNewCommand?.Refresh();
        Message = editId != null ? "Editing a saved cursor." : "";
        RenderNow();
    }

    private void Changed() { _timer.Stop(); _timer.Start(); }

    private CursorSpec Current(string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"), Name = string.IsNullOrWhiteSpace(Name) ? "My cursor" : Name.Trim(), Kind = CursorKind.Builder,
        Layers = Layers.Select(l => l.Clone()).ToList(), CenterHotspot = !Layers.Any(l => l.Shape == CursorShape.Arrow),
    };

    private void RenderNow()
    {
        var spec = Current();
        Preview = CursorRenderer.Render(spec, 256, 0, checker: true);
        PreviewReal = CursorRenderer.Render(spec, 64, 0, checker: true);
    }

    private void Save(bool asNew)
    {
        if (Layers.Count == 0) { Message = "The cursor has no shapes yet."; return; }
        try
        {
            string? id = !asNew && _editingId != null ? _editingId : null;
            var spec = Current(id);
            CursorLibrary.Save(spec);
            _editingId = spec.Id;
            OnPropertyChanged(nameof(Heading)); OnPropertyChanged(nameof(CanSaveAsNew)); SaveAsNewCommand.Refresh();
            Message = $"“{spec.Name}” saved to your cursors.";
            Saved?.Invoke(spec.Id);
        }
        catch (Exception ex) { Message = "Couldn't save the cursor: " + ex.Message; }
    }
}

// ============================================================ picture to cursor

public sealed class ImageCursorViewModel : ObservableObject
{
    private BitmapSource? _original, _result;
    private string _path = "", _notes = "", _message = "", _name = "Custom cursor";
    private bool _busy;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private bool _removeBg = true, _keepInside = true, _specks = true, _clean = true, _pixel;
    private double _strength = 45, _sharpen;

    public BitmapSource? Original { get => _original; private set => Set(ref _original, value); }
    public BitmapSource? Result { get => _result; private set { Set(ref _result, value); OnPropertyChanged(nameof(HasResult)); } }
    public bool HasPicture => _original != null;
    public bool HasResult => _result != null;
    public string FileName => _path.Length == 0 ? "No picture yet" : Path.GetFileName(_path);
    public string Notes { get => _notes; private set => Set(ref _notes, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

    public bool RemoveBackground { get => _removeBg; set { if (Set(ref _removeBg, value)) Reprocess(); } }
    public double Strength { get => _strength; set { if (Set(ref _strength, Math.Round(value))) Reprocess(); } }
    public bool KeepInside { get => _keepInside; set { if (Set(ref _keepInside, value)) Reprocess(); } }
    public bool RemoveSpecks { get => _specks; set { if (Set(ref _specks, value)) Reprocess(); } }
    public bool CleanEdges { get => _clean; set { if (Set(ref _clean, value)) Reprocess(); } }
    public bool PixelArt { get => _pixel; set { if (Set(ref _pixel, value)) Reprocess(); } }
    public double Sharpen { get => _sharpen; set { if (Set(ref _sharpen, Math.Round(value))) Reprocess(); } }

    public RelayCommand ChooseCommand { get; }
    public RelayCommand DiscardCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand SaveFilesCommand { get; }
    public event Action<string>? Added;

    public ImageCursorViewModel()
    {
        _timer.Tick += async (_, _) => { _timer.Stop(); await ProcessAsync(); };
        ChooseCommand = new RelayCommand(Choose);
        DiscardCommand = new RelayCommand(() => { _path = ""; Original = null; Result = null; Notes = ""; Message = ""; OnPropertyChanged(nameof(HasPicture)); OnPropertyChanged(nameof(FileName)); });
        AddCommand = new RelayCommand(Add, () => HasResult);
        SaveFilesCommand = new RelayCommand(SaveFiles, () => HasResult);
    }

    private void Choose()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import cursor image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > 60 * 1024 * 1024) { Message = "The image is larger than 60 MB."; return; }
            _path = dlg.FileName;
            Original = ImageCursorProcessor.Load(_path);
            Name = Path.GetFileNameWithoutExtension(_path);
            OnPropertyChanged(nameof(HasPicture)); OnPropertyChanged(nameof(FileName));
            Message = "";
            Reprocess();
        }
        catch (Exception ex) { Message = "Couldn't open the image: " + ex.Message; }
    }

    private void Reprocess() { if (_original != null) { _timer.Stop(); _timer.Start(); } }

    private async Task ProcessAsync()
    {
        if (_original == null) return;
        IsBusy = true;
        var src = _original;
        var o = new ImageCursorOptions(RemoveBackground, Strength, KeepInside, RemoveSpecks, CleanEdges, PixelArt, Sharpen);
        try
        {
            // Freeze-copy so the worker thread can read it.
            var res = await Sta.RunAsync(() => ImageCursorProcessor.Process(src, o, 128));
            Result = res.Image; Notes = res.Notes;
            AddCommand.Refresh(); SaveFilesCommand.Refresh();
        }
        catch (Exception ex) { Log.Error("Processing a cursor image failed", ex); Message = "The image couldn't be processed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void Add()
    {
        if (_result == null) return;
        try
        {
            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(CursorLibrary.Folder);
            var file = id + ".png";
            File.WriteAllBytes(Path.Combine(CursorLibrary.Folder, file), CursorRenderer.ToPng(_result));
            var spec = new CursorSpec { Id = id, Name = string.IsNullOrWhiteSpace(Name) ? "Custom cursor" : Name.Trim(), Kind = CursorKind.Image, ImageFile = file, CenterHotspot = false };
            CursorLibrary.Save(spec);
            Message = $"“{spec.Name}” added to your cursors.";
            Added?.Invoke(id);
        }
        catch (Exception ex) { Message = "Couldn't save the cursor: " + ex.Message; }
    }

    private void SaveFiles()
    {
        if (_result == null) return;
        try
        {
            Directory.CreateDirectory(CursorsViewModel.ExportFolder);
            var safe = string.Concat(Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            File.WriteAllBytes(Path.Combine(CursorsViewModel.ExportFolder, safe + ".png"), CursorRenderer.ToPng(_result));
            var set = new Dictionary<int, byte[]>();
            foreach (var s in new[] { 32, 48, 64, 128 }) set[s] = CursorRenderer.ToPng(new TransformedBitmap(_result, new ScaleTransform(s / 128.0, s / 128.0)));
            File.WriteAllBytes(Path.Combine(CursorsViewModel.ExportFolder, safe + ".cur"), CursorRenderer.ToCur(set, false));
            Message = "Exported to " + CursorsViewModel.ExportFolder;
        }
        catch (Exception ex) { Message = "Export failed: " + ex.Message; }
    }
}

// ============================================================ fonts

public sealed class FontsViewModel : ObservableObject
{
    private readonly RobloxLocationViewModel _loc;
    private List<FontEntry> _all = new();
    private FontEntry? _selected;
    private string _search = "", _sample = "Quick foxes jump 1234567890", _message = "";
    private bool _loading = true;

    public ObservableCollection<FontEntry> Items { get; } = new();
    public RobloxSettings Settings => Svc.S.Roblox;
    public FontEntry? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(CanRemove)); } }
    }
    public bool HasSelection => _selected != null;
    public bool CanRemove => _selected is { IsUser: true };
    public string Detail => _selected == null ? "" : $"{_selected.Styles} style{(_selected.Styles == 1 ? "" : "s")} · {_selected.Source}" + (Settings.FontId == _selected.File ? " · Used in Roblox" : "") + (General.FontName == _selected.Name ? " · Used in Nighty" : "");
    public GeneralSettings General => Svc.S.General;
    public string Search { get => _search; set { if (Set(ref _search, value)) Filter(); } }
    public string Sample { get => _sample; set => Set(ref _sample, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }
    public string Count => $"{Items.Count} font{(Items.Count == 1 ? "" : "s")}";

    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public AsyncCommand UseInRobloxCommand { get; }
    public AsyncCommand DefaultRobloxCommand { get; }
    public RelayCommand UseInNightyCommand { get; }
    public RelayCommand DefaultNightyCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand ExportCommand { get; }

    public FontsViewModel(RobloxLocationViewModel loc)
    {
        _loc = loc;
        AddCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import font", Filter = "Fonts|*.ttf;*.otf;*.ttc" };
            if (dlg.ShowDialog() != true) return;
            var (e, err) = FontLibrary.Add(dlg.FileName);
            if (e == null) { Message = err ?? "Couldn't add the font."; return; }
            _all.Insert(0, e); Filter(); Selected = e; Message = $"“{e.Name}” added.";
        });
        RemoveCommand = new RelayCommand(() =>
        {
            if (Selected is not { IsUser: true } f) return;
            var err = FontLibrary.Remove(f);
            if (err != null) { Message = err; return; }
            _all.Remove(f); Filter(); Message = $"“{f.Name}” removed from Nighty.";
        });
        UseInRobloxCommand = new AsyncCommand(async () =>
        {
            if (Selected == null) return;
            Message = "Adding to Roblox…";
            var f = Selected;
            var r = await Task.Run(() => Svc.RobloxAssets.ApplyFont(f.File));
            if (r.Changed > 0) { Settings.FontId = f.File; Message = $"“{f.Name}” added to Roblox. " + r.Note + (r.Error != null ? " But one place failed: " + r.Error : ""); }
            else Message = r.Error ?? "Nothing was changed.";
            OnPropertyChanged(nameof(Detail)); _loc.Refresh();
        }, () => Selected != null);
        DefaultRobloxCommand = new AsyncCommand(async () =>
        {
            var r = await Task.Run(Svc.RobloxAssets.RestoreFont);
            if (r.Error != null) Message = r.Error; else { Settings.FontId = ""; Message = "Roblox's own fonts are back."; }
            OnPropertyChanged(nameof(Detail));
        });
        UseInNightyCommand = new RelayCommand(() =>
        {
            if (Selected == null) return;
            General.FontName = Selected.Name; ThemeService.ApplyFont(Selected.Name);
            Message = $"Nighty now uses {Selected.Name}."; OnPropertyChanged(nameof(Detail));
        });
        DefaultNightyCommand = new RelayCommand(() => { General.FontName = ""; ThemeService.ApplyFont(""); Message = "Back to the default font."; OnPropertyChanged(nameof(Detail)); });
        InstallCommand = new RelayCommand(() => { if (Selected != null) Message = FontLibrary.InstallForUser(Selected); });
        ExportCommand = new RelayCommand(() =>
        {
            if (Selected == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export font", FileName = Path.GetFileName(Selected.File), Filter = "Font file|*" + Path.GetExtension(Selected.File) };
            if (dlg.ShowDialog() != true) return;
            try { File.Copy(Selected.File, dlg.FileName, true); Message = "Saved " + Path.GetFileName(dlg.FileName); } catch (Exception ex) { Message = ex.Message; }
        });
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        // System.Windows.Media font enumeration must run on the UI thread, so only yield first to let the page paint.
        await Task.Yield();
        _all = FontLibrary.Load();
        Filter();
        IsLoading = false;
    }

    private void Filter()
    {
        var q = _search.Trim();
        var keep = Selected;
        Items.Clear();
        foreach (var f in _all.Where(f => q.Length == 0 || f.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))) Items.Add(f);
        OnPropertyChanged(nameof(Count));
        if (keep != null && Items.Contains(keep)) Selected = keep;
    }
}
