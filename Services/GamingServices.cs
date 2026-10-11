using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Native;

namespace Nighty.Services;

public sealed record OptionResult(StatusKind Kind, string Text);

/// <summary>
/// Game Mode: applies only the optimisations the user selected, remembers original values, and restores them
/// when disabled, on exit, or after a crash (original values are persisted while active).
/// </summary>
public sealed class GameModeService
{
    private const string HighPerfGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private readonly Dictionary<int, ProcessPriorityClass> _origPriority = new();
    private bool _timerActive, _awakeActive;
    private readonly Dictionary<int, ProcessPriorityClass> _origCalm = new();
    private readonly HashSet<int> _freedFor = new();

    /// <summary>Background apps that wait while Roblox runs. Discord and music players are deliberately not here.</summary>
    private static readonly HashSet<string> Background = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "msedgewebview2", "steam", "steamwebhelper", "epicgameslauncher",
        "epicwebhelper", "battle.net", "eadesktop", "ubisoftconnect", "galaxyclient", "onedrive", "dropbox", "googledrivefs", "teams", "ms-teams",
        "microsoftedgeupdate", "googleupdate", "phoneexperiencehost", "widgets", "searchhost",
    };
    /// <summary>Apps currently slowed, and memory given back since Game Mode was switched on.</summary>
    public int AppsSlowed => _origCalm.Count;
    public double MemoryFreedMb { get; private set; }
    private System.Windows.Threading.DispatcherTimer? _watch;

    public bool IsActive { get; private set; }
    public Dictionary<string, OptionResult> Results { get; } = new();
    public event Action? Changed;

    private SystemBackups Backups => Svc.S.Backups;
    private GameSettings Opt => Svc.S.Game;

    /// <summary>Called at startup: if the previous run ended while Game Mode was active, undo what it left behind.</summary>
    public void RecoverFromCrash()
    {
        if (!Backups.GameModeActive) return;
        Log.Warn("Previous session ended with Game Mode active; restoring system settings");
        RestorePersistentSettings();
    }

    public async Task SetActiveAsync(bool active)
    {
        if (active == IsActive) return;
        if (active) await ActivateAsync(); else await DeactivateAsync();
        Changed?.Invoke();
    }

    private async Task ActivateAsync()
    {
        Results.Clear();
        Backups.GameModeActive = true;
        Svc.Settings.Save();
        IsActive = true;

        if (Opt.HighPerformancePower) Results["power"] = await Task.Run(ApplyPower);
        MemoryFreedMb = 0; _freedFor.Clear();
        if (Opt.RobloxHighPriority) Results["priority"] = ApplyPriority();
        if (Opt.CalmBackgroundApps) Results["calm"] = ApplyCalm();
        if (Opt.FreeMemory) Results["memory"] = FreeBackgroundMemory();
        if (Opt.RobloxHighPriority || Opt.CalmBackgroundApps || Opt.FreeMemory) StartWatcher();
        if (Opt.TimerResolution)
        {
            _timerActive = NativeMethods.timeBeginPeriod(1) == 0;
            Results["timer"] = _timerActive ? new(StatusKind.Success, "1 ms timer resolution requested") : new(StatusKind.Error, "Windows rejected the timer request");
        }
        if (Opt.WindowsGameMode) Results["gamemode"] = ApplyGameBar();
        if (Opt.KeepDisplayAwake)
        {
            _awakeActive = NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_DISPLAY_REQUIRED | NativeMethods.ES_SYSTEM_REQUIRED) != 0;
            Results["awake"] = _awakeActive ? new(StatusKind.Success, "Display and system kept awake") : new(StatusKind.Error, "Windows rejected the request");
        }
        Log.Info("Game Mode enabled: " + string.Join(", ", Results.Select(r => $"{r.Key}={r.Value.Kind}")));
    }

    private async Task DeactivateAsync()
    {
        _watch?.Stop(); _watch = null;
        RestorePriority();
        if (_timerActive) { NativeMethods.timeEndPeriod(1); _timerActive = false; }
        if (_awakeActive) { NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS); _awakeActive = false; }
        await Task.Run(RestorePersistentSettings);
        IsActive = false;
        Results.Clear();
        Log.Info("Game Mode disabled; original settings restored");
    }

    /// <summary>Synchronous restore used on app exit.</summary>
    public void RestoreOnExit()
    {
        if (!IsActive && !Backups.GameModeActive) return;
        _watch?.Stop();
        RestorePriority();
        if (_timerActive) { NativeMethods.timeEndPeriod(1); _timerActive = false; }
        if (_awakeActive) { NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS); _awakeActive = false; }
        RestorePersistentSettings();
        IsActive = false;
    }

    // ---- power plan ----
    private OptionResult ApplyPower()
    {
        try
        {
            var (_, active) = Run("powercfg", "/getactivescheme");
            var m = Regex.Match(active, @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
            if (!m.Success) return new(StatusKind.Error, "Could not read the current power plan");
            var original = m.Value.ToLowerInvariant();
            if (original == HighPerfGuid) return new(StatusKind.Success, "High performance plan already active");

            var (list, listing) = Run("powercfg", "/list");
            if (!listing.ToLowerInvariant().Contains(HighPerfGuid))
                return new(StatusKind.Warning, "This PC has no High performance plan (Modern Standby); skipped");

            Backups.OriginalPowerScheme = original;
            Svc.Settings.Save();
            var (code, outp) = Run("powercfg", "/setactive " + HighPerfGuid);
            return code == 0 ? new(StatusKind.Success, "Switched to High performance power plan")
                             : new(StatusKind.Error, "powercfg failed: " + outp.Trim());
        }
        catch (Exception ex) { Log.Error("Power plan change failed", ex); return new(StatusKind.Error, ex.Message); }
    }

    // ---- Roblox priority ----
    private OptionResult ApplyPriority()
    {
        var procs = Process.GetProcessesByName(RobloxService.ProcessName);
        if (procs.Length == 0) return new(StatusKind.Info, "Waiting for Roblox to start");
        int ok = 0, failed = 0;
        foreach (var p in procs)
        {
            try
            {
                if (!_origPriority.ContainsKey(p.Id)) _origPriority[p.Id] = p.PriorityClass;
                p.PriorityClass = ProcessPriorityClass.High;
                ok++;
            }
            catch { failed++; }
            finally { p.Dispose(); }
        }
        return failed == 0 ? new(StatusKind.Success, $"Roblox set to High priority ({ok} process{(ok == 1 ? "" : "es")})")
                           : new(StatusKind.Warning, $"{ok} set, {failed} could not be changed (access denied)");
    }

    private void StartWatcher()
    {
        _watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _watch.Tick += (_, _) =>
        {
            if (Opt.RobloxHighPriority) Results["priority"] = ApplyPriority();
            if (Opt.CalmBackgroundApps) Results["calm"] = ApplyCalm();
            if (Opt.FreeMemory) Results["memory"] = FreeBackgroundMemory();
            Changed?.Invoke();
        };
        _watch.Start();
    }

    // ---- calm background apps ----
    private OptionResult ApplyCalm()
    {
        var roblox = Process.GetProcessesByName(RobloxService.ProcessName);
        bool running = roblox.Length > 0;
        foreach (var r in roblox) r.Dispose();
        if (!running) { RestoreCalm(); return new(StatusKind.Info, "Waiting for Roblox to start"); }
        int failed = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!Background.Contains(p.ProcessName) || p.Id == Environment.ProcessId) continue;
                if (_origCalm.ContainsKey(p.Id)) continue;
                var cur = p.PriorityClass;
                if (cur is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal) continue;
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
                _origCalm[p.Id] = cur;
            }
            catch { failed++; }
            finally { p.Dispose(); }
        }
        // Forget processes that have exited.
        foreach (var pid in _origCalm.Keys.ToList()) { try { using var gone = Process.GetProcessById(pid); } catch { _origCalm.Remove(pid); } }
        return new(StatusKind.Success, _origCalm.Count == 0 ? "No background apps needed slowing" : $"{_origCalm.Count} background app process{(_origCalm.Count == 1 ? "" : "es")} waiting" + (failed > 0 ? $" ({failed} could not be changed)" : ""));
    }

    private void RestoreCalm()
    {
        foreach (var (pid, prio) in _origCalm)
        {
            try { using var p = Process.GetProcessById(pid); p.PriorityClass = prio; } catch { /* gone */ }
        }
        _origCalm.Clear();
    }

    // ---- free up memory ----
    private OptionResult FreeBackgroundMemory()
    {
        var roblox = Process.GetProcessesByName(RobloxService.ProcessName);
        var pids = roblox.Select(p => p.Id).ToList();
        foreach (var p in roblox) p.Dispose();
        if (pids.Count == 0) return new(StatusKind.Info, "Waiting for Roblox to start");
        if (pids.All(_freedFor.Contains)) return new(StatusKind.Success, $"{MemoryFreedMb:0} MB given back by background apps");
        foreach (var id in pids) _freedFor.Add(id);
        double freed = 0;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!Background.Contains(p.ProcessName) || p.Id == Environment.ProcessId) continue;
                long before = p.WorkingSet64;
                if (!NativeMethods.EmptyWorkingSet(p.Handle)) continue;
                p.Refresh();
                freed += Math.Max(0, before - p.WorkingSet64) / 1048576.0;
            }
            catch { }
            finally { p.Dispose(); }
        }
        MemoryFreedMb += freed;
        return new(StatusKind.Success, $"{MemoryFreedMb:0} MB given back by background apps");
    }

    private void RestorePriority()
    {
        RestoreCalm();
        foreach (var (pid, prio) in _origPriority)
        {
            try { using var p = Process.GetProcessById(pid); p.PriorityClass = prio; } catch { /* process already gone */ }
        }
        _origPriority.Clear();
    }

    // ---- Windows Game Mode flag ----
    private OptionResult ApplyGameBar()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
            var cur = key.GetValue("AutoGameModeEnabled");
            if (!Backups.HasGameBarBackup)
            {
                Backups.HasGameBarBackup = true;
                Backups.OriginalGameBarValue = cur is int i ? i : null;
                Svc.Settings.Save();
            }
            if (cur is int v && v == 1) return new(StatusKind.Success, "Windows Game Mode already on");
            key.SetValue("AutoGameModeEnabled", 1, RegistryValueKind.DWord);
            return new(StatusKind.Success, "Windows Game Mode turned on");
        }
        catch (Exception ex) { Log.Error("Game Mode flag failed", ex); return new(StatusKind.Error, ex.Message); }
    }

    private void RestorePersistentSettings()
    {
        try
        {
            if (!string.IsNullOrEmpty(Backups.OriginalPowerScheme))
            {
                Run("powercfg", "/setactive " + Backups.OriginalPowerScheme);
                Backups.OriginalPowerScheme = null;
            }
            if (Backups.HasGameBarBackup)
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
                if (Backups.OriginalGameBarValue is int v) key.SetValue("AutoGameModeEnabled", v, RegistryValueKind.DWord);
                else key.DeleteValue("AutoGameModeEnabled", false);
                Backups.HasGameBarBackup = false;
                Backups.OriginalGameBarValue = null;
            }
        }
        catch (Exception ex) { Log.Error("Restoring Game Mode settings failed", ex); }
        Backups.GameModeActive = false;
        Svc.Settings.Save();
    }

    private static (int Code, string Output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(8000);
        return (p.ExitCode, o);
    }
}

public sealed class CleanerCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool NeedsAdmin { get; init; }
    public Func<string[]> Roots { get; init; } = () => Array.Empty<string>();
    public Func<FileInfo, bool>? Filter { get; init; }
    public bool IsRecycleBin { get; init; }
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;
}

public sealed record ScannedFile(string Path, long Size, string Root);

public sealed class CategoryScan
{
    public List<ScannedFile> Files { get; } = new();
    public long Bytes { get; set; }
    public int SkippedInaccessible { get; set; }
    public string? Note { get; set; }
    public int Count => Files.Count;
}

public sealed class CleanResult
{
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public long BytesFreed { get; set; }
    public bool Cancelled { get; set; }
}

/// <summary>
/// Cleaner. Only scans a fixed list of known cache/temp locations, shows exactly what would be removed, and only
/// ever deletes files from that scan (re-validated to still be inside their scanned root).
/// </summary>
public sealed class CleanerService
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public IReadOnlyList<CleanerCategory> Categories { get; } = new List<CleanerCategory>
    {
        new() { Id = "user_temp", Name = "User temporary files", Description = "Files in your %TEMP% folder older than 1 hour.",
                Roots = () => new[] { Path.GetTempPath() }, MinAge = TimeSpan.FromHours(1) },
        new() { Id = "win_temp", Name = "Windows temporary files", Description = "C:\\Windows\\Temp (older than 1 hour). Needs administrator rights for most files.",
                NeedsAdmin = true, Roots = () => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") }, MinAge = TimeSpan.FromHours(1) },
        new() { Id = "thumbs", Name = "Thumbnail cache", Description = "Explorer thumbnail and icon cache databases; Windows rebuilds them.",
                Roots = () => new[] { Path.Combine(Local, "Microsoft", "Windows", "Explorer") },
                Filter = f => f.Name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase) },
        new() { Id = "shader", Name = "DirectX shader cache", Description = "Compiled shader cache; games rebuild it on next launch.",
                Roots = () => new[] { Path.Combine(Local, "D3DSCache") } },
        new() { Id = "crash", Name = "Crash dumps", Description = "Memory dumps left by crashed applications.",
                Roots = () => new[] { Path.Combine(Local, "CrashDumps") } },
        new() { Id = "roblox_logs", Name = "Roblox logs", Description = "Roblox client log files (older than 1 hour).",
                Roots = () => new[] { Path.Combine(Local, "Roblox", "logs") }, MinAge = TimeSpan.FromHours(1) },
        new() { Id = "chrome", Name = "Chrome cache", Description = "Chrome's page, code and GPU caches for every profile. Not passwords, history or cookies. Close Chrome first for the best result.",
                Roots = () => BrowserCaches(Path.Combine(Local, "Google", "Chrome", "User Data")) },
        new() { Id = "edge", Name = "Edge cache", Description = "Edge's page, code and GPU caches for every profile. Not passwords, history or cookies. Close Edge first for the best result.",
                Roots = () => BrowserCaches(Path.Combine(Local, "Microsoft", "Edge", "User Data")) },
        new() { Id = "brave", Name = "Brave cache", Description = "Brave's page, code and GPU caches for every profile. Not passwords, history or cookies.",
                Roots = () => BrowserCaches(Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data")) },
        new() { Id = "firefox", Name = "Firefox cache", Description = "Firefox's page cache for every profile. Not passwords, history or cookies. Close Firefox first for the best result.",
                Roots = () => ChildDirs(Path.Combine(Local, "Mozilla", "Firefox", "Profiles"), "cache2") },
        new() { Id = "discord", Name = "Discord cache", Description = "Discord's image, code and GPU caches; rebuilt as you use it. Not your messages or login.",
                Roots = () => new[] { "Cache", "Code Cache", "GPUCache" }.Select(n => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", n)).ToArray() },
        new() { Id = "gpu_cache", Name = "NVIDIA / AMD shader caches", Description = "Graphics-driver shader caches (DXCache, GLCache); games and drivers rebuild them.",
                Roots = () => new[] { Path.Combine(Local, "NVIDIA", "DXCache"), Path.Combine(Local, "NVIDIA", "GLCache"), Path.Combine(Local, "AMD", "DxCache"), Path.Combine(Local, "AMD", "GLCache") } },
        new() { Id = "steam_cache", Name = "Steam web cache", Description = "Steam's built-in browser cache; rebuilt when you open the store or community.",
                Roots = () => new[] { Path.Combine(Local, "Steam", "htmlcache") } },
        new() { Id = "wer", Name = "Windows error reports", Description = "Queued and archived error reports Windows keeps after crashes.",
                Roots = () => new[] { Path.Combine(Local, "Microsoft", "Windows", "WER") } },
        new() { Id = "winupdate", Name = "Windows Update downloads", Description = "Already-installed update downloads in the Windows SoftwareDistribution Download folder (older than 1 day). Needs administrator rights.",
                NeedsAdmin = true, Roots = () => new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download") }, MinAge = TimeSpan.FromDays(1) },
        new() { Id = "recycle", Name = "Recycle Bin", Description = "Permanently empties the Recycle Bin on all drives.", IsRecycleBin = true },
    };

    private static readonly string[] BrowserCacheFolders = { "Cache", "Code Cache", "GPUCache" };

    /// <summary>The cache folders of every Chromium profile (Default, Profile 1, ...), never the profile itself.</summary>
    private static string[] BrowserCaches(string userData)
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(userData)) return Array.Empty<string>();
            foreach (var profile in Directory.EnumerateDirectories(userData))
            {
                string name = Path.GetFileName(profile);
                if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                foreach (var f in BrowserCacheFolders) list.Add(Path.Combine(profile, f));
            }
        }
        catch { }
        return list.ToArray();
    }

    /// <summary>One named sub-folder inside each child of a folder (Firefox: Profiles\*\cache2).</summary>
    private static string[] ChildDirs(string parent, string child)
    {
        try { return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).Select(d => Path.Combine(d, child)).ToArray() : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public async Task<Dictionary<string, CategoryScan>> ScanAsync(IEnumerable<string> ids, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new Dictionary<string, CategoryScan>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var cat = Categories.First(c => c.Id == id);
            progress?.Report("Scanning " + cat.Name + "…");
            result[id] = await Task.Run(() => Scan(cat, ct), ct);
        }
        return result;
    }

    private CategoryScan Scan(CleanerCategory cat, CancellationToken ct)
    {
        var scan = new CategoryScan();
        if (cat.IsRecycleBin)
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            if (NativeMethods.SHQueryRecycleBin(null, ref info) == 0)
            {
                scan.Bytes = info.i64Size;
                scan.Note = $"{info.i64NumItems} item(s) in the Recycle Bin";
                if (info.i64NumItems > 0) scan.Files.Add(new ScannedFile("Recycle Bin", info.i64Size, "recycle"));
            }
            else scan.Note = "Could not query the Recycle Bin";
            return scan;
        }

        foreach (var root in cat.Roots())
        {
            if (!Directory.Exists(root)) { scan.Note ??= "Folder not found – nothing to clean"; continue; }
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos(); }
                catch { scan.SkippedInaccessible++; continue; }
                foreach (var e in entries)
                {
                    try
                    {
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // never follow links
                        if (e is DirectoryInfo d) { stack.Push(d.FullName); continue; }
                        var f = (FileInfo)e;
                        if (cat.Filter != null && !cat.Filter(f)) continue;
                        if (cat.MinAge > TimeSpan.Zero && DateTime.UtcNow - f.LastWriteTimeUtc < cat.MinAge) continue;
                        scan.Files.Add(new ScannedFile(f.FullName, f.Length, root));
                        scan.Bytes += f.Length;
                    }
                    catch { scan.SkippedInaccessible++; }
                }
            }
        }
        return scan;
    }

    public async Task<CleanResult> CleanAsync(Dictionary<string, CategoryScan> scans, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var res = new CleanResult();
            int total = scans.Values.Sum(s => s.Count), done = 0;
            foreach (var (id, scan) in scans)
            {
                var cat = Categories.First(c => c.Id == id);
                if (cat.IsRecycleBin)
                {
                    if (scan.Count > 0)
                    {
                        int hr = NativeMethods.SHEmptyRecycleBin(IntPtr.Zero, null, NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
                        if (hr == 0) { res.Deleted++; res.BytesFreed += scan.Bytes; } else res.Skipped++;
                    }
                    progress?.Report((++done, total));
                    continue;
                }
                foreach (var f in scan.Files)
                {
                    if (ct.IsCancellationRequested) { res.Cancelled = true; return res; }
                    try
                    {
                        // Re-validate: still inside the scanned root, still a regular file.
                        var full = Path.GetFullPath(f.Path);
                        if (!full.StartsWith(Path.GetFullPath(f.Root), StringComparison.OrdinalIgnoreCase)) { res.Skipped++; continue; }
                        var info = new FileInfo(full);
                        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) { res.Skipped++; continue; }
                        long size = info.Length;
                        info.Delete();
                        res.Deleted++; res.BytesFreed += size;
                    }
                    catch (IOException) { res.Skipped++; }                 // in use
                    catch (UnauthorizedAccessException) { res.Skipped++; } // permission
                    catch (Exception ex) { res.Skipped++; Log.Warn("Cleaner skipped " + f.Path, ex); }
                    if (++done % 25 == 0) progress?.Report((done, total));
                }
            }
            progress?.Report((total, total));
            Log.Info($"Cleaner: deleted {res.Deleted}, skipped {res.Skipped}, freed {res.BytesFreed} bytes");
            return res;
        }, ct);
    }
}
