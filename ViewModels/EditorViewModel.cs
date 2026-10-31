using System.Collections.ObjectModel;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;

namespace Nighty.ViewModels;

public sealed class EditorViewModel : ObservableObject
{
    private EditItem? _sel;
    private string _message = "", _titleText = "My video";
    private StatusKind _kind = StatusKind.Info;
    private double _progress, _splitAt;
    private bool _busy;
    private int _height = 1080, _fps = 30, _quality = 1;
    private CancellationTokenSource? _cts;

    /// <summary>The video, picture and title clips, in order.</summary>
    public ObservableCollection<EditItem> Timeline { get; } = new();
    /// <summary>Music tracks that play under the whole video.</summary>
    public ObservableCollection<EditItem> Music { get; } = new();

    public EditItem? Selected
    {
        get => _sel;
        set
        {
            if (!Set(ref _sel, value)) return;
            if (value != null) SplitAt = Math.Round(value.Start + value.Length / 2, 2);
            OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanSplit)); OnPropertyChanged(nameof(IsTitle)); OnPropertyChanged(nameof(CanVolume));
            Previewed?.Invoke(value);
        }
    }
    public bool HasSelection => _sel != null;
    public bool IsTitle => _sel?.Kind == EditKind.Title;
    public bool CanVolume => _sel is { Kind: EditKind.Video or EditKind.Music };
    public bool CanSplit => _sel is { Kind: EditKind.Video };
    public double SplitAt { get => _splitAt; set => Set(ref _splitAt, value); }
    public string TitleText { get => _titleText; set => Set(ref _titleText, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); OnPropertyChanged(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;
    public string TotalText => $"Total length {Timeline.Sum(t => t.Length):0.0} s";

    public int[] Heights { get; } = { 480, 720, 1080, 2160 };
    public int[] FpsChoices { get; } = { 24, 30, 60 };
    public string[] Qualities { get; } = { "Smaller file", "Balanced", "Best quality" };
    public int Height { get => _height; set => Set(ref _height, value); }
    public int Fps { get => _fps; set => Set(ref _fps, value); }
    public int Quality { get => _quality; set => Set(ref _quality, value); }

    /// <summary>The page shows this clip in its preview.</summary>
    public event Action<EditItem?>? Previewed;
    public event Action<EditItem>? PreviewPlayRequested;

    public AsyncCommand AddVideosCommand { get; }
    public AsyncCommand AddPicturesCommand { get; }
    public AsyncCommand AddMusicCommand { get; }
    public RelayCommand AddTitleCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand SplitCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }

    public EditorViewModel()
    {
        AddVideosCommand = new AsyncCommand(() => AddFiles("Add videos", "Videos|" + Mask(EditorService.VideoExt), true, EditKind.Video));
        AddPicturesCommand = new AsyncCommand(() => AddFiles("Add pictures", "Pictures|" + Mask(EditorService.PictureExt), true, EditKind.Picture));
        AddMusicCommand = new AsyncCommand(() => AddFiles("Add music", "Music|" + Mask(EditorService.MusicExt), true, EditKind.Music));
        AddTitleCommand = new RelayCommand(() => { var t = EditorService.NewTitle(TitleText); Timeline.Add(t); Selected = t; Refresh(); });
        RemoveCommand = new RelayCommand(() =>
        {
            if (Selected == null) return;
            var s = Selected; int i = Timeline.IndexOf(s);
            if (!Timeline.Remove(s)) Music.Remove(s);
            Selected = Timeline.Count == 0 ? null : Timeline[Math.Min(i < 0 ? 0 : i, Timeline.Count - 1)];
            Refresh();
        });
        MoveUpCommand = new RelayCommand(() => Move(-1));
        MoveDownCommand = new RelayCommand(() => Move(1));
        SplitCommand = new RelayCommand(Split);
        PlayCommand = new RelayCommand(() => { if (Selected != null) PreviewPlayRequested?.Invoke(Selected); });
        ClearCommand = new RelayCommand(() => { Timeline.Clear(); Music.Clear(); Selected = null; Refresh(); Say("Timeline cleared.", StatusKind.Info); });
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        ExportCommand = new AsyncCommand(Export);
    }

    private static string Mask(string[] ext) => string.Join(";", ext.Select(e => "*" + e)) + "|All files|*.*";

    private async Task AddFiles(string title, string filter, bool multi, EditKind kind)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, Multiselect = multi };
        if (dlg.ShowDialog() != true) return;
        int added = 0;
        foreach (var f in dlg.FileNames)
        {
            try
            {
                var item = await EditorService.Probe(f, kind);
                if (item == null) continue;
                if (kind == EditKind.Music) Music.Add(item); else Timeline.Add(item);
                Selected = item; added++;
            }
            catch (Exception ex) { Log.Error("Editor couldn't read " + f, ex); Say($"Windows couldn't read “{System.IO.Path.GetFileName(f)}”.", StatusKind.Warning); }
        }
        Refresh();
        if (added > 0) Say($"Added {added}.", StatusKind.Success);
    }

    private void Move(int by)
    {
        if (Selected == null) return;
        int i = Timeline.IndexOf(Selected), j = i + by;
        if (i < 0 || j < 0 || j >= Timeline.Count) return;
        Timeline.Move(i, j); Refresh();
    }

    private void Split()
    {
        if (Selected is not { Kind: EditKind.Video } c) return;
        if (SplitAt <= c.Start + 0.1 || SplitAt >= c.End - 0.1) { Say("Pick a split point inside the clip (between its start and end).", StatusKind.Warning); return; }
        var second = new EditItem { Kind = EditKind.Video, Path = c.Path, Source = c.Source, End = c.End, Volume = c.Volume };
        second.Start = SplitAt;
        c.End = SplitAt;
        Timeline.Insert(Timeline.IndexOf(c) + 1, second);
        Selected = second; Refresh();
        Say("Split into two clips.", StatusKind.Success);
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(TotalText));
        foreach (var t in Timeline) t.PropertyChanged -= OnItem;
        foreach (var t in Timeline) t.PropertyChanged += OnItem;
    }
    private void OnItem(object? s, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(EditItem.Length)) OnPropertyChanged(nameof(TotalText)); }

    private void Say(string m, StatusKind k) { Message = m; Kind = k; }

    private async Task Export()
    {
        if (Timeline.Count == 0) { Say("There's nothing on the timeline to export yet.", StatusKind.Warning); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export video", Filter = "MP4 video (*.mp4)|*.mp4", FileName = $"Edit {DateTime.Now:yyyy-MM-dd HH-mm}", DefaultExt = ".mp4", InitialDirectory = SafeDir() };
        if (dlg.ShowDialog() != true) return;
        _cts = new CancellationTokenSource(); Busy = true; Progress = 0; Say("Exporting…", StatusKind.Info);
        var prog = new Progress<double>(p => { Progress = p; Message = $"Exporting {p:0}%"; });
        try
        {
            var all = Timeline.Concat(Music).ToList();
            var path = await EditorService.Export(all, dlg.FileName, new ExportOptions(Height, Fps, Quality), prog, _cts.Token);
            Progress = 100; Say("Your video is exported to " + path, StatusKind.Success);
        }
        catch (OperationCanceledException) { Say("Export cancelled.", StatusKind.Info); }
        catch (Exception ex) { Log.Error("Export failed", ex); Say("Export failed: " + ex.Message, StatusKind.Error); }
        finally { Busy = false; }
    }

    private static string SafeDir()
    {
        try { Directory.CreateDirectory(RecorderService.BaseFolder); return RecorderService.BaseFolder; } catch { return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos); }
    }
}
