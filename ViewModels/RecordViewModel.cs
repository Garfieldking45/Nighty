using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;
using Nighty.Views;

namespace Nighty.ViewModels;

public sealed record IntChoice(string Label, int Value);

public sealed class LibraryItemViewModel
{
    public required RecorderService.LibraryItem Item { get; init; }
    public string Name => Item.Name;
    public string Kind => Item.Kind;
    public string Detail => $"{Item.Kind} · {WhenText} · {SizeText}" + (Item.App.Length > 0 ? " · " + Item.App : "");
    public string WhenText => Item.When.Date == DateTime.Today ? $"Today {Item.When:HH:mm}" : Item.When.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {Item.When:HH:mm}" : Item.When.ToString("MMM d, HH:mm");
    public string SizeText => Item.Size >= 1 << 30 ? $"{Item.Size / 1073741824.0:0.0} GB" : $"{Item.Size / 1048576.0:0.0} MB";
}

public sealed class RecordViewModel : ObservableObject
{
    private int _tab;
    private string _message = "";
    private string _kindFilter = "All";
    private bool _busy;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public RecordSettings Settings => Svc.S.Record;
    public int Tab { get => _tab; set { if (Set(ref _tab, value) && value == 1) RefreshLibrary(); } }
    public string Message { get => _message; private set => Set(ref _message, value); }

    // ---------------- overview ----------------
    public bool ReplayOn
    {
        get => Svc.Recorder.ReplayOn;
        set { Svc.Recorder.SetReplay(value); Settings.ReplayOnAtStart = value; Svc.Toast.Toggled("Instant Replay", value); RefreshStatus(); }
    }
    public bool IsRecording => Svc.Recorder.IsRecording;
    public string StatusTitle => Svc.Recorder.IsRecording ? "Recording" : Svc.Recorder.ReplayOn ? "Instant Replay is on" : "Instant Replay is off";
    public string StatusDetail
    {
        get
        {
            var r = Svc.Recorder;
            if (r.IsRecording) return $"{r.RecordingTime:mm\\:ss}" + (r.DroppedFrames > 0 ? $" · {r.DroppedFrames} frames skipped to keep up" : "") + " · " + Settings.Fps + " FPS";
            if (r.ReplayOn)
            {
                double have = Math.Min(r.BufferedSeconds, Settings.ClipSeconds);
                return $"{(have >= Settings.ClipSeconds - 1 ? $"The last {Settings.ClipSeconds} s are ready to save" : $"Filling up: {have:0} of {Settings.ClipSeconds} s")} · {r.BufferedBytes / 1048576.0:0} MB in memory";
            }
            return "Turn it on to save the last moments with one key. Nothing is captured while it's off: no CPU, GPU or memory used.";
        }
    }
    public string RecordLabel => IsRecording ? "Stop and save" : "Start recording";
    public bool CanSaveClip => Svc.Recorder.ReplayOn && !_busy;
    public string EncoderText => Svc.Recorder.IsRecording ? Svc.Recorder.Encoder : "Windows H.264 encoder (hardware when your graphics card has one)";
    public string ReplayHint => $"Keeps the last {Settings.ClipSeconds} s in memory (never on disk). About {EstimateMb(Settings.ClipSeconds):0} MB at these settings.";

    public RelayCommand SaveClipCommand { get; }
    public RelayCommand ToggleRecordCommand { get; }
    public RelayCommand ToggleReplayCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    // ---------------- library ----------------
    public ObservableCollection<LibraryItemViewModel> Items { get; } = new();
    public string KindFilter { get => _kindFilter; set { if (Set(ref _kindFilter, value)) RefreshLibrary(); } }
    public string LibrarySummary { get; private set; } = "";
    public bool LibraryEmpty => Items.Count == 0;
    public RelayCommand PlayCommand { get; }
    public RelayCommand ShowInFolderCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand RefreshLibraryCommand { get; }
    public RelayCommand SaveCopyCommand { get; }

    // ---------------- source & quality ----------------
    public List<IntChoice> Resolutions { get; } = new() { new("Native", 0), new("1440p", 1440), new("1080p", 1080), new("720p", 720), new("480p", 480) };
    public List<IntChoice> FpsChoices { get; } = new() { new("30 FPS", 30), new("60 FPS", 60) };
    public List<IntChoice> Lengths { get; } = new() { new("15 seconds", 15), new("30 seconds", 30), new("1 minute", 60), new("2 minutes", 120) };
    public List<IntChoice> Qualities { get; } = new() { new("Light", 0), new("Balanced", 1), new("High", 2) };
    public int Resolution { get => Settings.Resolution; set { Settings.Resolution = value; Touch(); } }
    public int Fps { get => Settings.Fps; set { Settings.Fps = value; Touch(); } }
    public int ClipSeconds { get => Settings.ClipSeconds; set { Settings.ClipSeconds = value; Touch(); } }
    public int Quality { get => Settings.Quality; set { Settings.Quality = value; Touch(); } }
    public RecordSource Source { get => Settings.Source; set { Settings.Source = value; OnPropertyChanged(); OnPropertyChanged(nameof(SourceHint)); } }
    public string SourceHint => Settings.Source == RecordSource.Screen ? "Everything on your main screen." : "Records the Roblox window, and your screen while Roblox is closed.";
    public string QualityHint
    {
        get
        {
            int h = Settings.Resolution == 0 ? 1080 : Settings.Resolution, w = (int)(h * 16.0 / 9);
            double mbps = (w * (double)h * Settings.Fps * (Settings.Quality switch { 0 => 0.12, 2 => 0.40, _ => 0.22 })) / 1_000_000;
            return $"About {mbps:0.#} Mbps at {w}x{h}, {Settings.Fps} FPS: {mbps / 8 * 60:0} MB per minute of video. A 60 FPS recording needs a fast PC.";
        }
    }

    private void Touch()
    {
        foreach (var n in new[] { nameof(Resolution), nameof(Fps), nameof(ClipSeconds), nameof(Quality), nameof(QualityHint), nameof(ReplayHint) }) OnPropertyChanged(n);
    }

    private static double EstimateMb(int seconds)
    {
        var s = Svc.S.Record;
        int h = s.Resolution == 0 ? 1080 : s.Resolution;
        double perFrameKb = h * (h * 16.0 / 9) * (s.Quality switch { 0 => 0.04, 2 => 0.14, _ => 0.07 }) / 1024;
        return perFrameKb * s.Fps * seconds / 1024;
    }

    // ---------------- feedback & files ----------------
    public string FolderText => RecorderService.BaseFolder;
    public RelayCommand ChangeFolderCommand { get; }
    public RelayCommand TestNoteCommand { get; }
    public RelayCommand ResetHotkeysCommand { get; }

    public RecordViewModel()
    {
        SaveClipCommand = new RelayCommand(() => _ = SaveClip(), () => CanSaveClip);
        ToggleRecordCommand = new RelayCommand(() => _ = ToggleRecord());
        ToggleReplayCommand = new RelayCommand(() => { ReplayOn = !ReplayOn; OnPropertyChanged(nameof(ReplayOn)); });
        OpenFolderCommand = new RelayCommand(() => OpenPath(RecorderService.BaseFolder));
        RefreshLibraryCommand = new RelayCommand(RefreshLibrary);
        PlayCommand = new RelayCommand(p => { if (p is LibraryItemViewModel i) Launch(i.Item.Path); });
        ShowInFolderCommand = new RelayCommand(p => { if (p is LibraryItemViewModel i) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{i.Item.Path}\"") { UseShellExecute = true }); });
        DeleteCommand = new RelayCommand(p =>
        {
            if (p is not LibraryItemViewModel i) return;
            if (!Dialogs.Confirm("Delete this " + i.Kind.ToLowerInvariant() + "?", $"“{i.Name}” moves to the Recycle Bin.", "Delete", danger: true)) return;
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(i.Item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                Message = $"{i.Name} moved to the Recycle Bin."; RefreshLibrary();
            }
            catch { Message = "The file couldn't be deleted. It may be open in another app."; }
        });
        SaveCopyCommand = new RelayCommand(p =>
        {
            if (p is not LibraryItemViewModel i) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save a copy", FileName = i.Name, Filter = "MP4 video|*.mp4" };
            if (dlg.ShowDialog() != true) return;
            try { File.Copy(i.Item.Path, dlg.FileName, true); Message = "Saved a copy as " + Path.GetFileName(dlg.FileName); } catch (Exception ex) { Message = ex.Message; }
        });
        ChangeFolderCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should Nighty save clips?" };
            if (dlg.ShowDialog() != true) return;
            Settings.Folder = dlg.FolderName; OnPropertyChanged(nameof(FolderText)); Message = "Clips will be saved in " + dlg.FolderName;
        });
        TestNoteCommand = new RelayCommand(() => Feedback(true));
        ResetHotkeysCommand = new RelayCommand(() =>
        {
            Settings.ClipVk = 0x79; Settings.ClipMods = 0; Settings.RecordVk = 0x7B; Settings.RecordMods = 0; Settings.ReplayVk = 0x77; Settings.ReplayMods = 0;
            Message = "Record hotkeys reset.";
        });

        Svc.Hotkeys.Register("rec-clip", () => (Settings.ClipVk, Settings.ClipMods), d => { if (d) _ = SaveClip(); });
        Svc.Hotkeys.Register("rec-record", () => (Settings.RecordVk, Settings.RecordMods), d => { if (d) _ = ToggleRecord(); });
        Svc.Hotkeys.Register("rec-replay", () => (Settings.ReplayVk, Settings.ReplayMods), d => { if (d) { ReplayOn = !ReplayOn; OnPropertyChanged(nameof(ReplayOn)); } });
        Svc.Recorder.Changed += () => Svc.Dispatch(RefreshStatus);

        if (Settings.ReplayOnAtStart) Svc.Recorder.SetReplay(true);
        _timer.Tick += (_, _) => { if (Svc.Recorder.IsRunning) RefreshStatus(); };
        _timer.Start();
    }

    private void RefreshStatus()
    {
        foreach (var n in new[] { nameof(ReplayOn), nameof(IsRecording), nameof(StatusTitle), nameof(StatusDetail), nameof(RecordLabel), nameof(CanSaveClip), nameof(EncoderText) }) OnPropertyChanged(n);
        SaveClipCommand.Refresh();
    }

    private void Feedback(bool test = false)
    {
        if (Settings.OnScreenNote || test) Svc.Toast.Show(Settings.NoteText, "", true);
        if (Settings.ClipSound || test) Nighty.Controls.Sfx.Clip();
    }

    private async Task SaveClip()
    {
        if (_busy) return;
        if (!Svc.Recorder.ReplayOn) { Svc.Toast.Show("Instant Replay is off", "turn it on in Nighty to save clips", false); return; }
        _busy = true; RefreshStatus();
        try
        {
            var (ok, msg, path) = await Svc.Recorder.SaveClipAsync();
            if (ok) { Feedback(); Message = "Clip saved: " + Path.GetFileName(path); }
            else { Svc.Toast.Show("Clip", msg, false); Message = msg; }
        }
        finally { _busy = false; RefreshStatus(); if (Tab == 1) RefreshLibrary(); }
    }

    private async Task ToggleRecord()
    {
        if (Svc.Recorder.IsRecording)
        {
            var (ok, msg, path) = await Svc.Recorder.StopRecordingAsync();
            Message = ok ? $"Recording saved: {Path.GetFileName(path)}" : msg;
            if (ok) { Svc.Toast.Show("Recording saved", "", true); if (Settings.ClipSound) Nighty.Controls.Sfx.Clip(); } else Svc.Toast.Show("Recording", msg, false);
        }
        else
        {
            var (ok, msg) = Svc.Recorder.StartRecording();
            Message = msg;
            Svc.Toast.Toggled("Recording", ok);
        }
        RefreshStatus();
        if (Tab == 1) RefreshLibrary();
    }

    public void RefreshLibrary()
    {
        var all = RecorderService.Library();
        var shown = all.Where(i => KindFilter == "All" || i.Kind == (KindFilter == "Clips" ? "Clip" : "Recording")).Take(60);
        Items.Clear();
        foreach (var i in shown) Items.Add(new LibraryItemViewModel { Item = i });
        int clips = all.Count(i => i.Kind == "Clip"), recs = all.Count - clips;
        long bytes = all.Sum(i => i.Size);
        LibrarySummary = $"{clips} clip{(clips == 1 ? "" : "s")}, {recs} recording{(recs == 1 ? "" : "s")}, {(bytes >= 1 << 30 ? $"{bytes / 1073741824.0:0.0} GB" : $"{bytes / 1048576.0:0} MB")} in total";
        OnPropertyChanged(nameof(LibrarySummary)); OnPropertyChanged(nameof(LibraryEmpty));
    }

    private static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Opening a clip failed", ex); }
    }

    private static void OpenPath(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open folder failed", ex); }
    }
}
