using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Nighty.Controls;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Native;
using Nighty.Services;

namespace Nighty.ViewModels;

public sealed record InfoLine(string Label, string Value);

public sealed class SeasonRow : ObservableObject
{
    public required Season Season { get; init; }
    public string Name => Season.Name;
    public string Window => (Season.StartUtc == DateTime.MinValue ? "Until " : $"{Local(Season.StartUtc)} to ") + Local(Season.EndUtc);
    public string Status => Seasons.StatusOf(Season);
    public bool HasSound => Season.Id is Seasons.Halloween or Seasons.Christmas;
    public RelayCommand? Preview { get; init; }
    public RelayCommand? Sound { get; init; }
    public void Refresh() => OnPropertyChanged(nameof(Status));

    private static string Local(DateTime utc)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")).ToString("MMM d, h:mm tt") + " ET"; }
        catch { return utc.ToString("u"); }
    }
}

/// <summary>The owner-only page: live app and system details, the seasonal themes, logs and tools.</summary>
public sealed class OwnerViewModel : ObservableObject
{
    private int _tab;
    private string _log = "", _message = "";

    public int Tab { get => _tab; set { if (Set(ref _tab, value) && value == 2) LoadLog(); } }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string LogText { get => _log; private set => Set(ref _log, value); }

    public ObservableCollection<InfoLine> App { get; } = new();
    public ObservableCollection<InfoLine> System { get; } = new();
    public ObservableCollection<InfoLine> Features { get; } = new();
    public ObservableCollection<SeasonRow> SeasonRows { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand AutomaticThemeCommand { get; }
    public RelayCommand RefreshLogCommand { get; }
    public RelayCommand OpenDataCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CopyInfoCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public RelayCommand ForgetCommand { get; }

    public OwnerViewModel()
    {
        RefreshCommand = new RelayCommand(Refresh);
        RefreshLogCommand = new RelayCommand(LoadLog);
        AutomaticThemeCommand = new RelayCommand(() =>
        {
            // Back to whatever the season rules say: the running season's theme, or the player's own theme when none runs.
            var g = Svc.S.General;
            g.SeasonApplied = null;
            if (Seasons.IsSeasonTheme(g.Theme) && Seasons.Active == null) g.Theme = string.IsNullOrEmpty(g.PreSeasonTheme) ? "midnight" : g.PreSeasonTheme;
            Seasons.Sync(g);
            Svc.Settings.Save();
            ThemeService.ApplyFromSettings(true);
            Seasons.Roll();
            Refresh();
            Message = "Theme is following the season again.";
        });
        OpenDataCommand = new RelayCommand(() => OpenFolder(AppPaths.Root));
        OpenLogsCommand = new RelayCommand(() => OpenFolder(AppPaths.Logs));
        CopyInfoCommand = new RelayCommand(() =>
        {
            try { Clipboard.SetText(Dump()); Message = "Everything on this page was copied."; }
            catch { Message = "Couldn't use the clipboard. Try again."; }
        });
        CheckUpdatesCommand = new RelayCommand(async () => { Message = "Checking for updates..."; await UpdateService.CheckAsync(); Message = "Update check finished."; });
        SignOutCommand = new RelayCommand(() => OwnerService.SignOut());
        ForgetCommand = new RelayCommand(() => { Svc.S.General.OwnerToken = null; Svc.Settings.Save(); Message = "This PC will ask for the sign-in again next time."; });

        foreach (var s in Seasons.All)
        {
            var season = s;
            SeasonRows.Add(new SeasonRow
            {
                Season = season,
                Preview = new RelayCommand(() =>
                {
                    Svc.S.General.Theme = season.Id;
                    ThemeService.ApplyFromSettings(true);
                    Message = $"Showing the {season.Name} theme, background and sounds.";
                }),
                Sound = new RelayCommand(() => { Sfx.Preview(season.Id, "launch"); Message = $"Playing the {season.Name} launch sound."; }),
            });
        }
        Refresh();
    }

    public void Refresh()
    {
        App.Clear(); System.Clear(); Features.Clear();
        var asm = typeof(OwnerViewModel).Assembly;
        var p = Process.GetCurrentProcess();
        var exe = Environment.ProcessPath;
        App.Add(new("Version", AppVersion.Text(asm.GetName().Version)));
        App.Add(new("Built", exe != null && File.Exists(exe) ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm") : "?"));
        App.Add(new("Running for", Span(DateTime.Now - p.StartTime)));
        App.Add(new("Memory in use", $"{p.WorkingSet64 / 1048576.0:0} MB"));
        App.Add(new("Threads / handles", $"{p.Threads.Count} / {p.HandleCount}"));
        App.Add(new("Theme", $"{Svc.S.General.Theme}  ({Seasons.Active?.Name ?? "no season running"})"));
        App.Add(new("Data folder", AppPaths.Root));
        App.Add(new("Settings file", File.Exists(AppPaths.SettingsFile) ? $"{new FileInfo(AppPaths.SettingsFile).Length / 1024.0:0.#} KB" : "none"));
        try { var logs = Directory.Exists(AppPaths.Logs) ? new DirectoryInfo(AppPaths.Logs).GetFiles("*.log") : Array.Empty<FileInfo>(); App.Add(new("Log files", $"{logs.Length} ({logs.Sum(f => f.Length) / 1024.0:0} KB)")); } catch { }

        System.Add(new("Windows", RuntimeInformation.OSDescription));
        System.Add(new("Architecture", RuntimeInformation.OSArchitecture.ToString()));
        System.Add(new("Logical cores", Environment.ProcessorCount.ToString()));
        System.Add(new(".NET", RuntimeInformation.FrameworkDescription));
        System.Add(new("Machine", Environment.MachineName));
        System.Add(new("Elevation", Elevation.IsAdmin ? "Administrator" : "Standard user"));
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref mem)) System.Add(new("System memory", $"{(mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0:0.0} of {mem.ullTotalPhys / 1073741824.0:0.0} GB in use"));

        var c = Svc.S.Clicker;
        Features.Add(new("Roblox running", Svc.Roblox.IsRunning ? "Yes" : "No"));
        Features.Add(new("Roblox in front", Svc.Roblox.IsForeground ? "Yes" : "No"));
        Features.Add(new("Auto Clicker", Svc.Clicker.IsClicking ? "Clicking" : "Idle"));
        Features.Add(new("Clicker setup", $"{(c.UseRange ? $"{c.MinCps}-{c.MaxCps}" : c.Cps.ToString())} CPS, duty {c.DutyCycle}%"));
        Features.Add(new("Game Mode", Svc.GameMode.IsActive ? $"On ({Svc.GameMode.AppsSlowed} apps waiting)" : "Off"));
        Features.Add(new("Hotbar slot", Svc.Bow.CurrentSlot == 0 ? "Unknown" : Svc.Bow.CurrentSlot.ToString()));
        Features.Add(new("Discord status", Svc.Discord.Status));
        Features.Add(new("Sound effects", Svc.S.General.SoundEffects ? "On" : "Off"));
        Features.Add(new("Pointer speed now", NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSESPEED, 0, out int sp, 0) ? sp.ToString() : "?"));

        foreach (var r in SeasonRows) r.Refresh();
    }

    private void LoadLog()
    {
        try
        {
            var file = Directory.Exists(AppPaths.Logs) ? new DirectoryInfo(AppPaths.Logs).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault() : null;
            if (file == null) { LogText = "No log yet."; return; }
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new Queue<string>();
            string? line;
            while ((line = sr.ReadLine()) != null) { lines.Enqueue(line); if (lines.Count > 250) lines.Dequeue(); }
            LogText = file.Name + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) { LogText = "Couldn't read the log: " + ex.Message; }
    }

    private string Dump()
    {
        var sb = new StringBuilder();
        foreach (var (title, list) in new[] { ("App", App), ("System", System), ("Features", Features) })
        {
            sb.AppendLine("== " + title);
            foreach (var l in list) sb.AppendLine($"{l.Label}: {l.Value}");
        }
        sb.AppendLine("== Seasons");
        foreach (var r in SeasonRows) sb.AppendLine($"{r.Name}: {r.Window} ({r.Status})");
        return sb.ToString();
    }

    private static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min {t.Seconds} s";

    private static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("Open folder failed", ex); }
    }
}
