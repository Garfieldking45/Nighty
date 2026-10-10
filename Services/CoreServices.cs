using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Native;

namespace Nighty.Services;

/// <summary>Application-wide paths.</summary>
public static class AppPaths
{
    /// <summary>NIGHTY_DATA points the app at a separate data folder (used to test a build next to a running copy).</summary>
    public static readonly string Root = Environment.GetEnvironmentVariable("NIGHTY_DATA") is { Length: > 0 } dev
        ? dev : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nighty");
    public static readonly string Logs = Path.Combine(Root, "logs");
    public static readonly string SettingsFile = Path.Combine(Root, "settings.json");
    public static readonly string ModBackups = Path.Combine(Root, "mod-backups");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}

/// <summary>Minimal structured file logger.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppPaths.Logs, $"nighty-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string msg) => Write("INFO", msg, null);
    public static void Warn(string msg, Exception? ex = null) => Write("WARN", msg, ex);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);

    private static void Write(string level, string msg, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}{(ex != null ? " | " + ex : "")}{Environment.NewLine}";
                File.AppendAllText(FilePath, line);
            }
        }
        catch { /* logging must never throw */ }
        Debug.WriteLine($"[{level}] {msg} {ex?.Message}");
    }
}

/// <summary>Loads and persists <see cref="AppSettings"/> as JSON with debounced, atomic writes.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DispatcherTimer _debounce;
    private readonly HashSet<object> _watched = new(ReferenceEqualityComparer.Instance);

    public AppSettings Current { get; private set; } = new();

    public SettingsService()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Save(); };
    }

    public void Load()
    {
        AppPaths.EnsureCreated();
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings file unreadable; starting from defaults", ex);
            try { File.Move(AppPaths.SettingsFile, AppPaths.SettingsFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}"); } catch { }
            Current = new AppSettings();
        }
        Watch(Current);
    }

    public void Reset()
    {
        Current = new AppSettings();
        Save();
    }

    private const long MaxProfileBytes = 2 * 1024 * 1024;

    /// <summary>Writes the whole setup (clickers, macros, overlays...) as plain JSON. Windows backups stay on this PC.</summary>
    public void ExportProfile(string path)
    {
        Save();
        var clone = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Current, Json), Json)!;
        clone.Backups = new SystemBackups();
        clone.Mods.Clear();   // mod folders are paths on this PC
        File.WriteAllText(path, JsonSerializer.Serialize(clone, Json));
    }

    /// <summary>Replaces the saved setup with a profile file. Returns an error message, or null when it was written (restart to use it).</summary>
    public string? ImportProfile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return "The file is empty.";
            if (info.Length > MaxProfileBytes) return "The file is too big to be a profile.";
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            if (loaded == null) return "That file isn't a Nighty profile.";
            loaded.Backups = Current.Backups;   // never take another PC's system backups
            loaded.Mods = Current.Mods;
            _debounce.Stop();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(loaded, Json));
            Frozen = true;
            return null;
        }
        catch (Exception ex) { return "That file couldn't be read: " + ex.Message; }
    }

    public void MarkDirty() { _debounce.Stop(); _debounce.Start(); }

    /// <summary>Set after a profile import so the closing app can't overwrite the imported file.</summary>
    public bool Frozen { get; private set; }

    public void Save()
    {
        if (Frozen) return;
        try
        {
            AppPaths.EnsureCreated();
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex) { Log.Error("Saving settings failed", ex); }
    }

    /// <summary>Hooks change notifications through the whole settings graph so any edit schedules a save.</summary>
    private void Watch(object? node)
    {
        if (node == null || !_watched.Add(node)) return;
        if (node is INotifyPropertyChanged npc) npc.PropertyChanged += (_, _) => MarkDirty();
        if (node is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, e) =>
            {
                if (e.NewItems != null) foreach (var i in e.NewItems) Watch(i);
                MarkDirty();
            };
            if (node is System.Collections.IEnumerable items) foreach (var i in items) Watch(i);
        }
        foreach (var p in node.GetType().GetProperties())
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            var t = p.PropertyType;
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum || t.IsValueType || t.IsArray) continue;
            if (t.Namespace?.StartsWith("Nighty") == true || typeof(INotifyCollectionChanged).IsAssignableFrom(t))
                Watch(p.GetValue(node));
        }
    }
}

public static class Elevation
{
    /// <summary>Admins, and members of "Performance Log Users" (S-1-5-32-559), may start ETW traces without elevation.</summary>
    public static bool CanTraceEtw => IsAdmin || new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(new SecurityIdentifier("S-1-5-32-559"));

    /// <summary>One-time setup (one UAC prompt): adds the current user to Performance Log Users. Takes effect after signing out and in.</summary>
    public static Task<(bool Ok, string Message)> AddToPerformanceLogUsersAsync()
        => RunPowerShellAsync($"Add-LocalGroupMember -SID 'S-1-5-32-559' -Member '{WindowsIdentity.GetCurrent().Name.Replace("'", "''")}' -ErrorAction Stop");

    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>
    /// Runs a PowerShell script, asking for UAC approval when the app is not already elevated.
    /// The script reports its outcome through a result file, so success is never assumed.
    /// </summary>
    public static async Task<(bool Ok, string Message)> RunPowerShellAsync(string script, CancellationToken ct = default)
    {
        var result = Path.Combine(Path.GetTempPath(), $"nighty-{Guid.NewGuid():N}.txt");
        var wrapped = $"$ErrorActionPreference='Stop'; try {{ {script}; Set-Content -LiteralPath '{result}' -Value 'OK' }} " +
                      $"catch {{ Set-Content -LiteralPath '{result}' -Value $_.Exception.Message; exit 1 }}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (!IsAdmin) psi.Verb = "runas";
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Could not start PowerShell.");
            await proc.WaitForExitAsync(ct);
            var text = File.Exists(result) ? File.ReadAllText(result).Trim() : "";
            if (text == "OK") return (true, "OK");
            return (false, string.IsNullOrEmpty(text) ? $"PowerShell exited with code {proc.ExitCode}." : text);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Administrator approval was declined.");
        }
        catch (Exception ex)
        {
            Log.Error("Elevated PowerShell failed", ex);
            return (false, ex.Message);
        }
        finally { try { File.Delete(result); } catch { } }
    }

    public static bool RelaunchAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Exception ex) { Log.Warn("Relaunch as admin cancelled/failed", ex); return false; }
    }
}

/// <summary>Detects the real Roblox client state.</summary>
public sealed class RobloxService
{
    public const string ProcessName = "RobloxPlayerBeta";

    public bool IsRunning
    {
        get
        {
            var procs = Process.GetProcessesByName(ProcessName);
            bool any = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return any;
        }
    }

    // Resolving a process name allocates and costs milliseconds; the foreground pid rarely changes, so cache the
    // answer per pid (re-checked every 2 s in case the pid was reused). This runs on the clicker thread.
    private int _fgPid;
    private bool _fgIsRoblox;
    private long _fgStamp;

    public bool IsForeground
    {
        get
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            long now = Environment.TickCount64;
            if (pid == _fgPid && now - _fgStamp < 2000) return _fgIsRoblox;
            bool result;
            try { using var p = Process.GetProcessById((int)pid); result = p.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase); }
            catch { result = false; }
            _fgPid = (int)pid; _fgIsRoblox = result; _fgStamp = now;
            return result;
        }
    }

    public static bool IsOwnWindowForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>Folder holding RobloxPlayerBeta.exe: the running client first (works with Bloxstrap/Fishstrap/Froststrap
    /// too), otherwise the newest install in the usual per-user locations, or null.</summary>
    public string? FindVersionFolder()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                try { var dir = Path.GetDirectoryName(p.MainModule?.FileName); if (dir != null) return dir; }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] roots = { "Roblox", "Bloxstrap", "Fishstrap", "Froststrap", "Voidstrap" };
        return roots.Select(r => Path.Combine(local, r, "Versions"))
            .Where(Directory.Exists)
            .SelectMany(r => new DirectoryInfo(r).GetDirectories("version-*"))
            .Where(d => File.Exists(Path.Combine(d.FullName, "RobloxPlayerBeta.exe")))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => d.FullName)
            .FirstOrDefault();
    }
}

/// <summary>Polls global hotkeys (no OS hooks, nothing to leak or release on exit).</summary>
public sealed class HotkeyService
{
    private sealed class Binding
    {
        public Func<(int Vk, int Mods)> Key = null!;
        public Action<bool> OnChange = null!;
        public bool Down;
    }

    private readonly Dictionary<string, Binding> _bindings = new();
    private readonly DispatcherTimer _timer;

    /// <summary>True while a hotkey box is capturing input, so bindings don't fire.</summary>
    public bool Suspended { get; set; }

    public HotkeyService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(8) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>Registers a binding. <paramref name="onChange"/> receives true on press and false on release.</summary>
    public void Register(string id, Func<(int Vk, int Mods)> key, Action<bool> onChange)
        => _bindings[id] = new Binding { Key = key, OnChange = onChange, Down = IsDown(key()) };

    public void Unregister(string id) => _bindings.Remove(id);

    private void Poll()
    {
        if (Suspended) return;
        foreach (var b in _bindings.Values.ToArray())
        {
            bool down = IsDown(b.Key());
            if (down == b.Down) continue;
            b.Down = down;
            try { b.OnChange(down); } catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }
        }
    }

    public static bool IsDown((int Vk, int Mods) k)
    {
        if (k.Vk <= 0) return false;
        if ((NativeMethods.GetAsyncKeyState(k.Vk) & 0x8000) == 0) return false;
        if ((k.Mods & Hotkeys.Ctrl) != 0 && (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) == 0) return false;
        if ((k.Mods & Hotkeys.Alt) != 0 && (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) == 0) return false;
        if ((k.Mods & Hotkeys.Shift) != 0 && (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) == 0) return false;
        if ((k.Mods & Hotkeys.Win) != 0 && (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) == 0 && (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) == 0) return false;
        return true;
    }
}

/// <summary>Real CPU / memory readings.</summary>
public sealed class SystemUsageService
{
    private long _lastIdle, _lastKernel, _lastUser;
    private bool _primed;

    public (double Cpu, double RamPercent, double RamUsedGb, double RamTotalGb) Sample()
    {
        double cpu = 0;
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (_primed)
            {
                long di = idle - _lastIdle, dk = kernel - _lastKernel, du = user - _lastUser;
                long total = dk + du;
                if (total > 0) cpu = Math.Clamp((total - di) * 100.0 / total, 0, 100);
            }
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user; _primed = true;
        }
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        NativeMethods.GlobalMemoryStatusEx(ref mem);
        double total2 = mem.ullTotalPhys / 1073741824.0;
        double used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        return (cpu, mem.dwMemoryLoad, used, total2);
    }

    /// <summary>Working set of all Roblox client processes in MB, or null when not running.</summary>
    public double? RobloxMemoryMb()
    {
        var procs = Process.GetProcessesByName(RobloxService.ProcessName);
        if (procs.Length == 0) return null;
        double mb = 0;
        foreach (var p in procs) { try { mb += p.WorkingSet64 / 1048576.0; } catch { } p.Dispose(); }
        return mb;
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("Nighty") != null; }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue("Nighty", $"\"{Environment.ProcessPath}\"" + (Svc.S.General.StartMinimized ? " --minimized" : ""));
        else k.DeleteValue("Nighty", false);
    }
}
