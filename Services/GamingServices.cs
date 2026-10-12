using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Native;
using Nighty.Services.Booster;

namespace Nighty.Services;

public sealed record OptionResult(StatusKind Kind, string Text);

/// <summary>
/// Game Mode, as the rest of the app sees it. The work is done by <see cref="GameModeEngine"/>, which only applies what the user
/// approved, only where this PC supports it, records every change on disk before making it, and restores exactly what it changed
/// when Game Mode ends, when Nighty closes, or after a crash.
/// </summary>
public sealed class GameModeService
{
    private readonly GameModeEngine _engine;
    private readonly ChangeTransactions _journal;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _watch;

    public GameModeService(BoosterService booster)
    {
        var control = booster.Control;
        _journal = new ChangeTransactions(new FileJournalStore(Path.Combine(AppPaths.Root, "recovery-journal.json")), new DefaultTargetResolver(control), log: m => Log.Warn(m));
        _engine = new GameModeEngine(new GameModeDeps
        {
            Game = booster.Game, Processes = booster.Processes, Control = control, Targets = new DefaultTargetFactory(control), Journal = _journal,
            Resources = new NativeSessionResources(), Hardware = () => booster.Hardware.Current,
            RefreshPower = () => booster.Hardware.RefreshPower(), Memory = BoosterService.MemoryNow, Log = m => Log.Info(m),
        });
        _engine.Changed += () => Changed?.Invoke();
    }

    public GameModeEngine Engine => _engine;
    public ChangeTransactions Journal => _journal;
    public bool IsActive => _engine.IsActive;
    public event Action? Changed;

    /// <summary>Apps currently slowed, and memory given back since Game Mode was switched on.</summary>
    public int AppsSlowed => _engine.AppsSlowed;
    public double MemoryFreedMb => _engine.MemoryFreedMb;

    /// <summary>The outcome of each option, shaped for the Game Mode list.</summary>
    public Dictionary<string, OptionResult> Results
        => _engine.Reports.ToDictionary(r => r.ActionId, r => new OptionResult(Kind(r.State), r.Text));

    private static StatusKind Kind(ActionState s) => s switch
    {
        ActionState.Applied or ActionState.AlreadyInPlace => StatusKind.Success,
        ActionState.AppliedUnverified => StatusKind.Warning,
        ActionState.Waiting => StatusKind.Info,
        ActionState.Skipped => StatusKind.Neutral,
        _ => StatusKind.Error,
    };

    /// <summary>Set when Game Mode could not start because no plan has been approved yet.</summary>
    public bool NeedsPlanApproval { get; private set; }
    /// <summary>What the last restore did, in words, or null when it restored nothing or nothing has run.</summary>
    public string? RestoreSummary => _engine.LastRestore is { Total: > 0 } r ? r.Summary() : null;
    public RestoreReport? LastRestore => _engine.LastRestore;
    /// <summary>A message about anything an earlier run left unfinished, shown until it has been dealt with.</summary>
    public string? RecoveryNotice { get; private set; }

    private SystemBackups Backups => Svc.S.Backups;

    /// <summary>Called at startup: undoes whatever an earlier run left behind (crash, kill or restart).</summary>
    public void RecoverFromCrash()
    {
        try
        {
            var report = _engine.RecoverAtStartup();
            if (Backups.GameModeActive && report.Total == 0) RestoreLegacyBackups();
            else if (Backups.GameModeActive && report.FullyRestored) Backups.GameModeActive = false;

            if (report.Total > 0 || _journal.LoadResult.State is LoadState.Unreadable or LoadState.RecoveredFromBackup)
            {
                RecoveryNotice = _journal.LoadResult.State == LoadState.Unreadable
                    ? "Nighty could not read its recovery file, so it cannot tell what an earlier run changed. Check the power plan and process priorities if anything feels off."
                    : "Nighty found changes left by the previous run. " + report.Summary();
                if (!report.FullyRestored) RecoveryNotice += " It will try again the next time Nighty starts.";
            }
            Svc.Settings.Save();
        }
        catch (Exception ex) { Log.Error("Startup recovery failed", ex); RecoveryNotice = "Startup recovery hit an error: " + ex.Message; }
    }

    /// <summary>Game Mode sessions from older Nighty versions recorded only these two values.</summary>
    private void RestoreLegacyBackups()
    {
        Log.Warn("Previous session ended with Game Mode active (older format); restoring system settings");
        try
        {
            if (!string.IsNullOrEmpty(Backups.OriginalPowerScheme) && Guid.TryParse(Backups.OriginalPowerScheme, out var g))
            {
                var now = PowerSchemeTarget.Active();
                if (now != null && !string.Equals(now, Backups.OriginalPowerScheme, StringComparison.OrdinalIgnoreCase)) new PowerSchemeTarget().Write(g.ToString());
                Backups.OriginalPowerScheme = null;
            }
            if (Backups.HasGameBarBackup)
            {
                var t = new RegistryValueTarget(@"Software\Microsoft\GameBar", "AutoGameModeEnabled");
                t.Write(Backups.OriginalGameBarValue is int v ? RegistryValueTarget.Dword(v) : null);
                Backups.HasGameBarBackup = false; Backups.OriginalGameBarValue = null;
            }
        }
        catch (Exception ex) { Log.Error("Restoring older Game Mode settings failed", ex); }
        Backups.GameModeActive = false;
    }

    public IReadOnlySet<string> ApprovedActions() => GameChoices.Enabled(Svc.S.Game);

    public async Task SetActiveAsync(bool active)
    {
        await _gate.WaitAsync();
        try
        {
            if (active == IsActive) return;
            if (active)
            {
                if (!Svc.S.Booster.PlanReviewed) { NeedsPlanApproval = true; Log.Info("Game Mode not started: no plan has been approved yet"); Changed?.Invoke(); return; }
                NeedsPlanApproval = false;
                await Svc.Booster.EnsureScannedAsync();
                Backups.GameModeActive = true;
                Svc.Settings.Save();
                var approved = ApprovedActions();
                await Task.Run(() => _engine.Activate(approved));
                StartWatcher();
            }
            else await DeactivateCoreAsync();
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }

    private async Task DeactivateCoreAsync()
    {
        StopWatcher();
        var report = await Task.Run(_engine.Deactivate);
        FinishDeactivation(report);
    }

    private void FinishDeactivation(RestoreReport report)
    {
        if (report.FullyRestored && !_journal.HasPending) Backups.GameModeActive = false;
        else Log.Warn("Game Mode ended but not everything is back: " + report.Summary());
        Svc.Settings.Save();
    }

    private void StartWatcher()
    {
        StopWatcher();
        var cts = _watch = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            try { while (await timer.WaitForNextTickAsync(cts.Token)) { try { _engine.Tick(); } catch (Exception ex) { Log.Warn("Game Mode tick failed", ex); } } }
            catch (OperationCanceledException) { }
        });
    }

    private void StopWatcher() { try { _watch?.Cancel(); } catch { } _watch = null; }

    /// <summary>Synchronous restore used on app exit.</summary>
    public void RestoreOnExit()
    {
        StopWatcher();
        RestoreReport report = RestoreReport.Empty;
        if (_engine.IsActive) report = _engine.Deactivate();
        if (_journal.HasPending) report = _journal.RestoreAll();
        if (report.Total > 0 || Backups.GameModeActive) FinishDeactivation(report);
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
