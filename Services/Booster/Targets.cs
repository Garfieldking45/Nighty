using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Nighty.Native;

namespace Nighty.Services.Booster;

public enum ReadStatus
{
    /// <summary>The value was read. A null value means "not set" (a registry value that does not exist).</summary>
    Ok,
    /// <summary>The thing being changed no longer exists (a process that exited, or whose id now belongs to another program).</summary>
    Gone,
    Denied,
    Failed,
}

public readonly record struct SettingRead(ReadStatus Status, string? Value, string? Detail = null)
{
    public bool IsOk => Status == ReadStatus.Ok;
    public static SettingRead Of(string? value) => new(ReadStatus.Ok, value);
    public static SettingRead Gone(string? detail = null) => new(ReadStatus.Gone, null, detail);
}

public readonly record struct SettingWrite(bool Ok, string? Detail = null)
{
    public static readonly SettingWrite Success = new(true);
    public static SettingWrite Fail(string detail) => new(false, detail);
}

/// <summary>
/// One thing Nighty may change and later put back. Values are strings so a saved original survives a restart and can be
/// compared exactly; <c>null</c> means "not set".
/// </summary>
public interface ISettingTarget
{
    /// <summary>Stable text that identifies this target again after a restart (see <see cref="ITargetResolver"/>).</summary>
    string Key { get; }
    string Description { get; }
    SettingRead Read();
    SettingWrite Write(string? value);
}

/// <summary>Rebuilds a target from the key saved in the journal.</summary>
public interface ITargetResolver
{
    ISettingTarget? Resolve(string key);
}

/// <summary>Everything that can be changed about another program's process. Isolated so Game Mode can be tested without real processes.</summary>
public interface IProcessControl
{
    SettingRead GetPriority(ProcIdentity id);
    SettingWrite SetPriority(ProcIdentity id, string priority);
    SettingRead GetThrottle(ProcIdentity id);
    SettingWrite SetThrottle(ProcIdentity id, string state);
    /// <summary>Asks Windows to trim the process' working set. Returns the bytes given back, or null if it could not be done.</summary>
    long? TrimWorkingSet(ProcIdentity id);
}

/// <summary>Names of the priority classes as stored in the journal.</summary>
public static class PriorityNames
{
    public const string Idle = "idle", BelowNormal = "belownormal", Normal = "normal", AboveNormal = "abovenormal", High = "high", Realtime = "realtime";

    public static string? FromClass(uint cls) => cls switch
    {
        0x40 => Idle, 0x4000 => BelowNormal, 0x20 => Normal, 0x8000 => AboveNormal, 0x80 => High, 0x100 => Realtime, _ => null,
    };

    public static uint? ToClass(string? name) => name switch
    {
        Idle => 0x40u, BelowNormal => 0x4000u, Normal => 0x20u, AboveNormal => 0x8000u, High => 0x80u, Realtime => 0x100u, _ => null,
    };
}

/// <summary>The "efficiency mode" switches of a process, saved as "c{control}:s{state}" (for example c1:s1 = efficiency mode on).</summary>
public static class ThrottleState
{
    public const uint ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;
    public static string Encode(uint control, uint state) => $"c{control}:s{state}";

    public static bool TryDecode(string? text, out uint control, out uint state)
    {
        control = state = 0;
        if (text == null || !text.StartsWith('c')) return false;
        var parts = text[1..].Split(":s");
        return parts.Length == 2 && uint.TryParse(parts[0], out control) && uint.TryParse(parts[1], out state);
    }
}

// ------------------------------------------------------------------------------------------------------------------
// Real implementations
// ------------------------------------------------------------------------------------------------------------------

/// <summary>Changes real processes. Every operation opens the process, checks its creation time matches, and only then acts on that same handle.</summary>
public sealed class NativeProcessControl : IProcessControl
{
    private static IntPtr Open(ProcIdentity id, uint access, out SettingRead? failure)
    {
        failure = null;
        IntPtr h = BoosterNative.OpenProcess(access | BoosterNative.PROCESS_QUERY_LIMITED_INFORMATION, false, id.Pid);
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            failure = err == 87 /* invalid parameter: no such pid */ ? SettingRead.Gone("The process has exited")
                    : err == 5 ? new SettingRead(ReadStatus.Denied, null, "Access denied")
                    : new SettingRead(ReadStatus.Failed, null, "Win32 error " + err);
            return IntPtr.Zero;
        }
        if (!BoosterNative.GetProcessTimes(h, out long created, out long exited, out _, out _)) { BoosterNative.CloseHandle(h); failure = new SettingRead(ReadStatus.Failed, null, "Could not read the process start time"); return IntPtr.Zero; }
        if (created != id.StartFileTime || exited != 0)
        {
            BoosterNative.CloseHandle(h);
            failure = SettingRead.Gone("The process id now belongs to a different program");
            return IntPtr.Zero;
        }
        return h;
    }

    public SettingRead GetPriority(ProcIdentity id)
    {
        IntPtr h = Open(id, 0, out var fail);
        if (h == IntPtr.Zero) return fail!.Value;
        try
        {
            uint cls = BoosterNative.GetPriorityClass(h);
            var name = PriorityNames.FromClass(cls);
            return name == null ? new SettingRead(ReadStatus.Failed, null, "Unknown priority class 0x" + cls.ToString("X")) : SettingRead.Of(name);
        }
        finally { BoosterNative.CloseHandle(h); }
    }

    public SettingWrite SetPriority(ProcIdentity id, string priority)
    {
        if (PriorityNames.ToClass(priority) is not uint cls) return SettingWrite.Fail("Unknown priority " + priority);
        IntPtr h = Open(id, BoosterNative.PROCESS_SET_INFORMATION, out var fail);
        if (h == IntPtr.Zero) return SettingWrite.Fail(fail!.Value.Detail ?? "Could not open the process");
        try { return BoosterNative.SetPriorityClass(h, cls) ? SettingWrite.Success : SettingWrite.Fail("Windows refused (error " + Marshal.GetLastWin32Error() + ")"); }
        finally { BoosterNative.CloseHandle(h); }
    }

    public SettingRead GetThrottle(ProcIdentity id)
    {
        // GetProcessInformation needs the full query right; the limited one is refused.
        IntPtr h = Open(id, BoosterNative.PROCESS_QUERY_INFORMATION, out var fail);
        if (h == IntPtr.Zero) return fail!.Value;
        try
        {
            var st = new BoosterNative.PROCESS_POWER_THROTTLING_STATE { Version = 1 };   // the call fails (error 87) unless the version is filled in
            if (!BoosterNative.GetProcessInformation(h, BoosterNative.ProcessPowerThrottling, ref st, Marshal.SizeOf<BoosterNative.PROCESS_POWER_THROTTLING_STATE>()))
                return new SettingRead(ReadStatus.Failed, null, "Windows would not report the power-throttling state (error " + Marshal.GetLastWin32Error() + ")");
            return SettingRead.Of(ThrottleState.Encode(st.ControlMask, st.StateMask));
        }
        finally { BoosterNative.CloseHandle(h); }
    }

    public SettingWrite SetThrottle(ProcIdentity id, string state)
    {
        if (!ThrottleState.TryDecode(state, out uint control, out uint on)) return SettingWrite.Fail("Bad throttle state " + state);
        IntPtr h = Open(id, BoosterNative.PROCESS_SET_INFORMATION, out var fail);
        if (h == IntPtr.Zero) return SettingWrite.Fail(fail!.Value.Detail ?? "Could not open the process");
        try
        {
            var st = new BoosterNative.PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = control, StateMask = on };
            return BoosterNative.SetProcessInformation(h, BoosterNative.ProcessPowerThrottling, ref st, Marshal.SizeOf(st))
                ? SettingWrite.Success : SettingWrite.Fail("Windows refused (error " + Marshal.GetLastWin32Error() + ")");
        }
        finally { BoosterNative.CloseHandle(h); }
    }

    public long? TrimWorkingSet(ProcIdentity id)
    {
        IntPtr h = Open(id, BoosterNative.PROCESS_SET_QUOTA, out _);
        if (h == IntPtr.Zero) return null;
        try
        {
            long before = WorkingSetOf(id.Pid);
            if (!BoosterNative.EmptyWorkingSet(h)) return null;
            return Math.Max(0, before - WorkingSetOf(id.Pid));
        }
        finally { BoosterNative.CloseHandle(h); }
    }

    private static long WorkingSetOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.WorkingSet64; } catch { return 0; }
    }
}

public sealed class WindowsProcessSource : IProcessSource
{
    public IReadOnlyList<ProcEntry> List()
        => BoosterNative.ListProcesses().Select(p => new ProcEntry(p.Pid, p.Name, p.Parent)).ToList();

    public ProcDetails? Describe(int pid)
    {
        IntPtr h = BoosterNative.OpenProcess(BoosterNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!BoosterNative.GetProcessTimes(h, out long created, out long exited, out _, out _) || exited != 0) return null;
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            string? path = BoosterNative.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            return new ProcDetails(new ProcIdentity(pid, created), path);
        }
        finally { BoosterNative.CloseHandle(h); }
    }
}

/// <summary>
/// A per-user registry value. Reads and writes the real registry; <c>null</c> means the value does not exist.
/// Encoded as "dword:N" or "str:text" so the saved original keeps its type.
/// </summary>
public sealed class RegistryValueTarget : ISettingTarget
{
    private readonly RegistryKey _root;
    private readonly string _rootName, _subKey, _name;

    public RegistryValueTarget(string subKey, string name) : this(Registry.CurrentUser, "HKCU", subKey, name) { }

    internal RegistryValueTarget(RegistryKey root, string rootName, string subKey, string name)
    {
        _root = root; _rootName = rootName; _subKey = subKey; _name = name;
    }

    public string Key => $"reg|{_rootName}|{_subKey}|{_name}";
    public string Description => $"{_rootName}\\{_subKey} → {_name}";

    public static string Dword(int v) => "dword:" + v;
    public static string Str(string v) => "str:" + v;

    public SettingRead Read()
    {
        try
        {
            using var k = _root.OpenSubKey(_subKey);
            return SettingRead.Of(k == null ? null : Encode(k.GetValue(_name)));
        }
        catch (System.Security.SecurityException) { return new SettingRead(ReadStatus.Denied, null, "Access denied"); }
        catch (UnauthorizedAccessException) { return new SettingRead(ReadStatus.Denied, null, "Access denied"); }
        catch (Exception ex) { return new SettingRead(ReadStatus.Failed, null, ex.Message); }
    }

    private static string? Encode(object? v) => v switch { null => null, int i => Dword(i), string s => Str(s), var o => Str(o.ToString() ?? "") };

    public SettingWrite Write(string? value)
    {
        try
        {
            if (value == null)
            {
                using var k = _root.OpenSubKey(_subKey, writable: true);
                k?.DeleteValue(_name, throwOnMissingValue: false);
                return SettingWrite.Success;
            }
            using var key = _root.CreateSubKey(_subKey);
            if (value.StartsWith("dword:") && int.TryParse(value.AsSpan(6), out int d)) key.SetValue(_name, d, RegistryValueKind.DWord);
            else if (value.StartsWith("str:")) key.SetValue(_name, value[4..], RegistryValueKind.String);
            else return SettingWrite.Fail("Unsupported value " + value);
            return SettingWrite.Success;
        }
        catch (Exception ex) { return SettingWrite.Fail(ex.Message); }
    }
}

/// <summary>The active Windows power plan (a GUID).</summary>
public sealed class PowerSchemeTarget : ISettingTarget
{
    public string Key => "powerscheme";
    public string Description => "Active Windows power plan";

    public static string? Active()
    {
        if (BoosterNative.PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(p).ToString(); } finally { BoosterNative.LocalFree(p); }
    }

    public SettingRead Read() => Active() is { } g ? SettingRead.Of(g) : new SettingRead(ReadStatus.Failed, null, "Windows would not report the active power plan");

    public SettingWrite Write(string? value)
    {
        if (!Guid.TryParse(value, out var g)) return SettingWrite.Fail("Not a power plan id");
        uint rc = BoosterNative.PowerSetActiveScheme(IntPtr.Zero, ref g);
        return rc == 0 ? SettingWrite.Success : SettingWrite.Fail("Windows refused the power plan (error " + rc + ")");
    }

    /// <summary>Every power plan on this PC as (id, friendly name).</summary>
    public static List<(string Id, string Name)> List()
    {
        var plans = new List<(string, string)>();
        for (uint i = 0; i < 32; i++)
        {
            var g = Guid.Empty; uint size = (uint)Marshal.SizeOf<Guid>();
            if (BoosterNative.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, BoosterNative.ACCESS_SCHEME, i, ref g, ref size) != 0) break;
            plans.Add((g.ToString(), FriendlyName(g)));
        }
        return plans;
    }

    private static string FriendlyName(Guid g)
    {
        uint size = 0;
        BoosterNative.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0 || size > 4096) return g.ToString();
        var buf = new byte[size];
        return BoosterNative.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0
            ? Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0') : g.ToString();
    }
}

/// <summary>Windows 10/11 "Power mode" (best power efficiency / balanced / better performance / best performance).</summary>
public sealed class PowerOverlayTarget : ISettingTarget
{
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid BetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public string Key => "poweroverlay";
    public string Description => "Windows power mode";

    public static string? Actual() => BoosterNative.PowerGetActualOverlayScheme(out var g) == 0 ? g.ToString() : null;
    public static string? Effective() => BoosterNative.PowerGetEffectiveOverlayScheme(out var g) == 0 ? g.ToString() : null;

    public static PowerMode ToMode(string? guid)
    {
        if (!Guid.TryParse(guid, out var g)) return PowerMode.Unknown;
        return g == BestEfficiency ? PowerMode.BestPowerEfficiency : g == Balanced ? PowerMode.Balanced
             : g == BetterPerformance ? PowerMode.BetterPerformance : g == BestPerformance ? PowerMode.BestPerformance : PowerMode.Unknown;
    }

    public SettingRead Read() => Actual() is { } g ? SettingRead.Of(g) : new SettingRead(ReadStatus.Failed, null, "Windows does not report a power mode on this PC");

    public SettingWrite Write(string? value)
    {
        if (!Guid.TryParse(value, out var g)) return SettingWrite.Fail("Not a power mode id");
        uint rc = BoosterNative.PowerSetActiveOverlayScheme(g);
        return rc == 0 ? SettingWrite.Success : SettingWrite.Fail("Windows refused the power mode (error " + rc + ")");
    }
}

/// <summary>A process' priority class, tied to one process by creation time.</summary>
public sealed class ProcessPriorityTarget : ISettingTarget
{
    private readonly IProcessControl _ctl;
    private readonly ProcIdentity _id;
    private readonly string _name;
    public ProcessPriorityTarget(IProcessControl ctl, ProcIdentity id, string name = "") { _ctl = ctl; _id = id; _name = name; }
    public string Key => "procprio|" + _id;
    public string Description => $"Priority of {(_name.Length > 0 ? _name : "process")} ({_id.Pid})";
    public SettingRead Read() => _ctl.GetPriority(_id);
    public SettingWrite Write(string? value) => value == null ? SettingWrite.Fail("No priority given") : _ctl.SetPriority(_id, value);
}

/// <summary>A process' efficiency-mode / timer-throttling switches, tied to one process by creation time.</summary>
public sealed class ProcessThrottleTarget : ISettingTarget
{
    private readonly IProcessControl _ctl;
    private readonly ProcIdentity _id;
    private readonly string _name;
    public ProcessThrottleTarget(IProcessControl ctl, ProcIdentity id, string name = "") { _ctl = ctl; _id = id; _name = name; }
    public string Key => "procthrottle|" + _id;
    public string Description => $"Power throttling of {(_name.Length > 0 ? _name : "process")} ({_id.Pid})";
    public SettingRead Read() => _ctl.GetThrottle(_id);
    public SettingWrite Write(string? value) => value == null ? SettingWrite.Fail("No state given") : _ctl.SetThrottle(_id, value);
}

/// <summary>Rebuilds the journal's targets: registry values, the power plan, the power mode and per-process settings.</summary>
public sealed class DefaultTargetResolver : ITargetResolver
{
    private readonly IProcessControl _ctl;
    public DefaultTargetResolver(IProcessControl ctl) => _ctl = ctl;

    public ISettingTarget? Resolve(string key)
    {
        var parts = key.Split('|');
        switch (parts[0])
        {
            case "reg" when parts.Length == 4 && parts[1] == "HKCU": return new RegistryValueTarget(parts[2], parts[3]);
            case "powerscheme": return new PowerSchemeTarget();
            case "poweroverlay": return new PowerOverlayTarget();
            case "procprio" when parts.Length == 2 && ProcIdentity.TryParse(parts[1], out var p1): return new ProcessPriorityTarget(_ctl, p1);
            case "procthrottle" when parts.Length == 2 && ProcIdentity.TryParse(parts[1], out var p2): return new ProcessThrottleTarget(_ctl, p2);
            default: return null;
        }
    }
}
