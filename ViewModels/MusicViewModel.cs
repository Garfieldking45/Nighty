using System.Collections.ObjectModel;
using System.Windows.Threading;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Services;

namespace Nighty.ViewModels;

public sealed class MusicViewModel : ObservableObject
{
    private readonly MusicService _m = Svc.Music;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private CancellationTokenSource? _cts;
    private string _query = "", _link = "", _message = "", _engine = "";
    private bool _busy, _seeking;
    private double _pos;
    private int _tab;
    private StatusKind _kind = StatusKind.Info;

    public ObservableCollection<Track> Library => _m.Library;
    public ObservableCollection<Track> Results { get; } = new();

    public int Tab { get => _tab; set => Set(ref _tab, value); }
    public string Query { get => _query; set => Set(ref _query, value); }
    public string Link { get => _link; set => Set(ref _link, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public StatusKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); OnPropertyChanged(nameof(NotBusy)); } }
    public bool NotBusy => !_busy;
    public string Engine { get => _engine; private set => Set(ref _engine, value); }

    public string NowTitle => _m.Current?.Title ?? "Nothing playing";
    public string NowSubtitle => _m.Current == null ? "Add songs, then press play." : (_m.Current.State.Length > 0 ? _m.Current.State : _m.Current.Subtitle);
    public bool IsPlaying => _m.IsPlaying;
    public string PlayGlyph => _m.IsPlaying ? "" : "";
    public double Duration => Math.Max(1, _m.DurationSeconds);
    public double Position { get => _pos; set => Set(ref _pos, value); }
    public string TimeText => $"{Fmt(_pos)} / {Fmt(_m.DurationSeconds)}";
    public double Volume { get => _m.Volume * 100; set { _m.Volume = value / 100; OnPropertyChanged(); } }
    public bool Shuffle { get => _m.Shuffle; set { _m.Shuffle = value; OnPropertyChanged(); } }
    public RepeatMode[] RepeatChoices { get; } = Enum.GetValues<RepeatMode>();
    public RepeatMode Repeat { get => _m.Repeat; set { _m.Repeat = value; OnPropertyChanged(); } }
    public string CacheText => $"Downloaded songs use {_m.CacheBytes() / 1048576.0:0.#} MB.";

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PrevCommand { get; }
    public RelayCommand PlayTrackCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand AddResultCommand { get; }
    public RelayCommand AddAllResultsCommand { get; }
    public RelayCommand ClearCacheCommand { get; }
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand UpdateEngineCommand { get; }
    public RelayCommand CancelCommand { get; }

    private static string Fmt(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

    public MusicViewModel()
    {
        PlayPauseCommand = new RelayCommand(() => _m.TogglePause());
        NextCommand = new RelayCommand(() => _m.Next());
        PrevCommand = new RelayCommand(() => _m.Previous());
        PlayTrackCommand = new RelayCommand(p => { if (p is Track t) _ = _m.Play(t); });
        RemoveCommand = new RelayCommand(p => { if (p is Track t) _m.Remove(t); });
        AddFilesCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add music", Multiselect = true, Filter = "Music (*.mp3;*.m4a;*.aac;*.wav;*.wma;*.flac;*.ogg;*.opus)|*.mp3;*.m4a;*.aac;*.wav;*.wma;*.flac;*.ogg;*.opus|All files (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;
            Say($"Added {_m.AddFiles(dlg.FileNames)} songs.", StatusKind.Success);
        });
        AddFolderCommand = new RelayCommand(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add a music folder" };
            if (dlg.ShowDialog() != true) return;
            Say($"Added {_m.AddFiles(new[] { dlg.FolderName })} songs.", StatusKind.Success);
        });
        AddResultCommand = new RelayCommand(p => { if (p is Track t) { _m.Add(t); Say("Added to your library.", StatusKind.Success); } });
        AddAllResultsCommand = new RelayCommand(() => { foreach (var t in Results.ToList()) _m.Add(t); Say($"Added {Results.Count} songs to your library.", StatusKind.Success); }, () => Results.Count > 0);
        ClearCacheCommand = new RelayCommand(() => { _m.ClearCache(); OnPropertyChanged(nameof(CacheText)); Say("Cleared the music cache.", StatusKind.Success); });
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        SearchCommand = new AsyncCommand(() => Run("Looking through YouTube…", async ct =>
        {
            if (string.IsNullOrWhiteSpace(Query)) { Say("Type a song or artist first.", StatusKind.Warning); return; }
            var list = await _m.SearchYouTube(Query.Trim(), ct);
            Results.Clear(); foreach (var t in list) Results.Add(t);
            AddAllResultsCommand.Refresh();
            Say(list.Count == 0 ? "Nothing found." : $"Found {list.Count} songs.", list.Count == 0 ? StatusKind.Warning : StatusKind.Success);
        }));
        ImportCommand = new AsyncCommand(() => Run("Reading the link…", async ct =>
        {
            var list = await _m.ImportLink(Link, ct);
            foreach (var t in list) _m.Add(t);
            Say(list.Count == 0 ? "That link had no songs." : $"Added {list.Count} songs to your library.", list.Count == 0 ? StatusKind.Warning : StatusKind.Success);
            if (list.Count > 0) Link = "";
        }));
        UpdateEngineCommand = new AsyncCommand(() => Run("Updating the music engine…", async ct =>
        {
            await _m.EnsureYtDlp(null, force: true, ct);
            RefreshEngine(); Say("Music engine updated.", StatusKind.Success);
        }));

        _m.Changed += OnServiceChanged;
        _tick.Tick += (_, _) => { if (!_seeking) { Position = _m.PositionSeconds; OnPropertyChanged(nameof(TimeText)); } };
        _tick.Start();
        RefreshEngine();
        if (_m.Message.Length > 0) Say(_m.Message, StatusKind.Warning);
    }

    private void OnServiceChanged()
    {
        Svc.Dispatch(() =>
        {
            OnPropertyChanged(nameof(NowTitle)); OnPropertyChanged(nameof(NowSubtitle)); OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(PlayGlyph)); OnPropertyChanged(nameof(Duration)); OnPropertyChanged(nameof(Shuffle)); OnPropertyChanged(nameof(Repeat));
            OnPropertyChanged(nameof(CacheText));
            if (_m.Message.Length > 0 && _m.Message != Message) Say(_m.Message, StatusKind.Error);
        });
    }

    /// <summary>Called by the page when the position slider is dragged or clicked.</summary>
    public void BeginSeek() => _seeking = true;
    public void EndSeek(double seconds) { _m.Seek(seconds); Position = seconds; _seeking = false; OnPropertyChanged(nameof(TimeText)); }

    private void RefreshEngine() => Engine = _m.HasYtDlp ? "Music engine installed." : "The music engine (yt-dlp, about 20 MB) downloads the first time you search or play a YouTube song.";

    private void Say(string m, StatusKind k) { Message = m; Kind = k; }

    private async Task Run(string working, Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        Busy = true; Say(working, StatusKind.Info);
        try { await work(_cts.Token); }
        catch (OperationCanceledException) { Say("Cancelled.", StatusKind.Info); }
        catch (Exception ex) { Say(ex.Message, StatusKind.Error); Log.Error("Music action failed", ex); }
        finally { Busy = false; RefreshEngine(); OnPropertyChanged(nameof(CacheText)); }
    }
}
