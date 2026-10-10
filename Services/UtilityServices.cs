using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Native;

namespace Nighty.Services;

// ============================================================ Brightness

public sealed class DisplayInfo : ObservableObject
{
    private int _current;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Method { get; init; }       // "Built-in panel (WMI)" or "External monitor (DDC/CI)"
    public int Min { get; init; }
    public int Max { get; init; } = 100;
    public IntPtr Handle { get; init; }
    public bool Supported { get; init; } = true;
    public string? Reason { get; init; }
    public int Current { get => _current; set => Set(ref _current, value); }
    public override string ToString() => Name;
}

/// <summary>Brightness through supported APIs only: WMI for built-in laptop panels, DDC/CI for external monitors.</summary>
public sealed class BrightnessService
{
    private readonly List<NativeMethods.PHYSICAL_MONITOR> _handles = new();

    public Task<List<DisplayInfo>> EnumerateAsync() => Task.Run(Enumerate);

    private List<DisplayInfo> Enumerate()
    {
        ReleaseHandles();
        var list = new List<DisplayInfo>();

        // Built-in panel
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightness");
            int i = 0;
            foreach (ManagementObject mo in s.Get())
            {
                var levels = (byte[])mo["Level"];
                list.Add(new DisplayInfo
                {
                    Id = "wmi:" + mo["InstanceName"], Name = i == 0 ? "Built-in display" : $"Built-in display {i + 1}",
                    Method = "Built-in panel (WMI)", Min = levels.Length > 0 ? levels.Min() : 0, Max = levels.Length > 0 ? levels.Max() : 100,
                    Current = Convert.ToInt32(mo["CurrentBrightness"]),
                });
                i++;
            }
        }
        catch (Exception ex) { Log.Info("WMI brightness unavailable: " + ex.Message); }

        // External monitors (DDC/CI)
        int idx = 0;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, IntPtr _, IntPtr _) =>
        {
            idx++;
            try
            {
                if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out var n) || n == 0) return true;
                var arr = new NativeMethods.PHYSICAL_MONITOR[n];
                if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;
                foreach (var pm in arr)
                {
                    _handles.Add(pm);
                    var name = string.IsNullOrWhiteSpace(pm.szPhysicalMonitorDescription) ? $"Monitor {idx}" : pm.szPhysicalMonitorDescription;
                    if (NativeMethods.GetMonitorBrightness(pm.hPhysicalMonitor, out var min, out var cur, out var max) && max > min)
                    {
                        list.Add(new DisplayInfo { Id = $"ddc:{idx}:{name}", Name = $"{name} (display {idx})", Method = "External monitor (DDC/CI)",
                            Min = (int)min, Max = (int)max, Current = (int)cur, Handle = pm.hPhysicalMonitor });
                    }
                    else if (!list.Any(d => d.Method.StartsWith("Built-in") && name.Contains("Generic PnP", StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add(new DisplayInfo { Id = $"ddc:{idx}:{name}", Name = $"{name} (display {idx})", Method = "External monitor (DDC/CI)",
                            Supported = false, Reason = "This display does not report DDC/CI brightness control (it may be disabled in the monitor's menu, or the connection doesn't support it)." });
                    }
                }
            }
            catch (Exception ex) { Log.Warn("DDC/CI enumeration failed", ex); }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    public bool TrySet(DisplayInfo d, int value, out string? error)
    {
        error = null;
        value = Math.Clamp(value, d.Min, d.Max);
        try
        {
            if (d.Method.StartsWith("Built-in"))
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject mo in s.Get())
                {
                    if (!d.Id.EndsWith(mo["InstanceName"]?.ToString() ?? "?", StringComparison.OrdinalIgnoreCase)) continue;
                    mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)value });
                    d.Current = value;
                    return true;
                }
                error = "Display not found.";
                return false;
            }
            if (NativeMethods.SetMonitorBrightness(d.Handle, (uint)value)) { d.Current = value; return true; }
            error = "The monitor rejected the brightness command.";
            return false;
        }
        catch (Exception ex) { error = ex.Message; Log.Warn("Set brightness failed", ex); return false; }
    }

    private void ReleaseHandles()
    {
        if (_handles.Count == 0) return;
        try { NativeMethods.DestroyPhysicalMonitors((uint)_handles.Count, _handles.ToArray()); } catch { }
        _handles.Clear();
    }
}

// ============================================================ Movement + Tracking helpers

/// <summary>
/// Movement Helper: keeps Windows accessibility shortcuts (Sticky/Filter/Toggle Keys) from interrupting WASD play,
/// and optionally speeds up key repeat. Original values are captured once and can be restored.
/// </summary>
public sealed class MovementService
{
    private const uint HotkeyActive = 0x4, ConfirmHotkey = 0x8;
    private SystemBackups B => Svc.S.Backups;

    public (uint Sticky, uint Filter, uint Toggle, int Delay, int Speed) ReadCurrent()
    {
        var sk = new NativeMethods.STICKYKEYS { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.STICKYKEYS>() };
        var fk = new NativeMethods.FILTERKEYS { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FILTERKEYS>() };
        var tk = new NativeMethods.TOGGLEKEYS { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.TOGGLEKEYS>() };
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETSTICKYKEYS, sk.cbSize, ref sk, 0);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETFILTERKEYS, fk.cbSize, ref fk, 0);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETTOGGLEKEYS, tk.cbSize, ref tk, 0);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETKEYBOARDDELAY, 0, out int delay, 0);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETKEYBOARDSPEED, 0, out int speed, 0);
        return (sk.dwFlags, fk.dwFlags, tk.dwFlags, delay, speed);
    }

    private void CaptureOriginal()
    {
        if (B.HasMovementBackup) return;
        var c = ReadCurrent();
        B.StickyFlags = c.Sticky; B.FilterFlags = c.Filter; B.ToggleFlags = c.Toggle; B.KeyboardDelay = c.Delay; B.KeyboardSpeed = c.Speed;
        B.HasMovementBackup = true;
        Svc.Settings.Save();
    }

    public bool HasBackup => B.HasMovementBackup;

    public string Apply(MovementSettings m)
    {
        try
        {
            CaptureOriginal();
            var cur = ReadCurrent();
            uint f = NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE;

            var sk = new NativeMethods.STICKYKEYS { cbSize = 8, dwFlags = m.DisableStickyKeysShortcut ? cur.Sticky & ~(HotkeyActive | ConfirmHotkey) : B.StickyFlags };
            var fk = new NativeMethods.FILTERKEYS { cbSize = 24, dwFlags = m.DisableFilterKeysShortcut ? cur.Filter & ~(HotkeyActive | ConfirmHotkey) : B.FilterFlags };
            // Preserve the other FILTERKEYS fields
            var fkCur = new NativeMethods.FILTERKEYS { cbSize = 24 };
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETFILTERKEYS, 24, ref fkCur, 0);
            fk.iWaitMSec = fkCur.iWaitMSec; fk.iDelayMSec = fkCur.iDelayMSec; fk.iRepeatMSec = fkCur.iRepeatMSec; fk.iBounceMSec = fkCur.iBounceMSec;
            var tk = new NativeMethods.TOGGLEKEYS { cbSize = 8, dwFlags = m.DisableToggleKeysShortcut ? cur.Toggle & ~(HotkeyActive | ConfirmHotkey) : B.ToggleFlags };

            bool ok = NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETSTICKYKEYS, 8, ref sk, f)
                    & NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETFILTERKEYS, 24, ref fk, f)
                    & NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETTOGGLEKEYS, 8, ref tk, f);
            ok &= NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETKEYBOARDDELAY, (uint)(m.FastKeyRepeat ? 0 : B.KeyboardDelay), IntPtr.Zero, f);
            ok &= NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETKEYBOARDSPEED, (uint)(m.FastKeyRepeat ? 31 : B.KeyboardSpeed), IntPtr.Zero, f);
            return ok ? "Applied." : "Windows rejected one or more settings.";
        }
        catch (Exception ex) { Log.Error("Movement apply failed", ex); return ex.Message; }
    }

    public string Restore()
    {
        if (!B.HasMovementBackup) return "Nothing to restore — no changes have been made.";
        uint f = NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE;
        var sk = new NativeMethods.STICKYKEYS { cbSize = 8, dwFlags = B.StickyFlags };
        var tk = new NativeMethods.TOGGLEKEYS { cbSize = 8, dwFlags = B.ToggleFlags };
        var fk = new NativeMethods.FILTERKEYS { cbSize = 24 };
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETFILTERKEYS, 24, ref fk, 0);
        fk.dwFlags = B.FilterFlags;
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETSTICKYKEYS, 8, ref sk, f);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETFILTERKEYS, 24, ref fk, f);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETTOGGLEKEYS, 8, ref tk, f);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETKEYBOARDDELAY, (uint)B.KeyboardDelay, IntPtr.Zero, f);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETKEYBOARDSPEED, (uint)B.KeyboardSpeed, IntPtr.Zero, f);
        B.HasMovementBackup = false;
        Svc.Settings.Save();
        return "Original keyboard and accessibility settings restored.";
    }
}

/// <summary>Tracking Helper: pointer speed and "Enhance pointer precision" through SystemParametersInfo.</summary>
public sealed class PointerService
{
    private SystemBackups B => Svc.S.Backups;
    public bool HasBackup => B.HasPointerBackup;

    public (int Speed, bool Precision) ReadCurrent()
    {
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSESPEED, 0, out int speed, 0);
        var mouse = new int[3];
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSE, 0, mouse, 0);
        return (speed, mouse[2] != 0);
    }

    public string Apply(int speed, bool precision)
    {
        try
        {
            if (!B.HasPointerBackup)
            {
                var mouse0 = new int[3];
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSE, 0, mouse0, 0);
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSESPEED, 0, out int s0, 0);
                B.MouseParams = mouse0; B.PointerSpeed = s0; B.HasPointerBackup = true;
                Svc.Settings.Save();
            }
            var mouse = new int[3];
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSE, 0, mouse, 0);
            if (precision) { mouse[0] = B.MouseParams.Length == 3 && B.MouseParams[2] != 0 ? B.MouseParams[0] : 6; mouse[1] = B.MouseParams.Length == 3 && B.MouseParams[2] != 0 ? B.MouseParams[1] : 10; mouse[2] = 1; }
            else { mouse[0] = 0; mouse[1] = 0; mouse[2] = 0; }
            uint f = NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE;
            ApplyScrollAndDoubleClick(f);
            bool ok = NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, mouse, f)
                    & NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(speed, 1, 20), f);
            if (!ok) return "Windows rejected the pointer settings.";
            var now = ReadCurrent();
            return now.Speed == speed && now.Precision == precision ? "Applied and verified." : "Applied, but Windows reports different values than requested.";
        }
        catch (Exception ex) { Log.Error("Pointer apply failed", ex); return ex.Message; }
    }

    private bool _autoApplied;

    /// <summary>Scroll speed (lines per notch) and double-click time; 0 in the settings leaves Windows' value alone.</summary>
    private void ApplyScrollAndDoubleClick(uint flags)
    {
        var t = Svc.S.Utility.Tracking;
        if (t.ScrollLines > 0)
        {
            if (B.ScrollLinesOriginal < 0 && NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWHEELSCROLLLINES, 0, out int lines, 0)) B.ScrollLinesOriginal = lines;
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWHEELSCROLLLINES, (uint)t.ScrollLines, IntPtr.Zero, flags);
        }
        if (t.DoubleClickMs > 0)
        {
            if (B.DoubleClickOriginal < 0) B.DoubleClickOriginal = (int)NativeMethods.GetDoubleClickTime();
            NativeMethods.SetDoubleClickTime((uint)t.DoubleClickMs);
        }
        Svc.Settings.Save();
    }

    private bool _slowApplied;
    private int _speedBeforeSlow;
    private readonly object _slowGate = new();

    /// <summary>Slot-1 slowdown: drops the pointer speed while slot 1 is selected in Roblox and puts it back after.
    /// Not persisted (no registry write), so a crash can't leave a slow pointer after the next sign-in.</summary>
    public void UpdateSlotSlowdown()
    {
        lock (_slowGate)
        {
            var t = Svc.S.Utility.Tracking;
            bool want = t.SlowInSlotOne && Svc.Bow.CurrentSlot == 1 && Svc.Roblox.IsForeground;
            if (want && !_slowApplied)
            {
                if (!B.HasPointerBackup) Apply(ReadCurrent().Speed, ReadCurrent().Precision);   // record originals so Restore works
                _speedBeforeSlow = ReadCurrent().Speed;
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(t.SlowSpeed, 1, 20), 0);
                _slowApplied = true;
            }
            else if (!want && _slowApplied)
            {
                _slowApplied = false;
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(_speedBeforeSlow, 1, 20), 0);
            }
        }
    }

    /// <summary>Called a couple of times per second. Applies the tracking settings while Roblox is focused and puts Windows' values back afterwards.</summary>
    public void AutoTick()
    {
        var t = Svc.S.Utility.Tracking;
        UpdateSlotSlowdown();
        bool want = t.ApplyOnlyInRoblox && Svc.Roblox.IsForeground;
        if (want && !_autoApplied)
        {
            _autoApplied = true;
            Apply(t.PointerSpeed, t.EnhancePointerPrecision);
        }
        else if (!want && _autoApplied)
        {
            _autoApplied = false;
            Restore();
        }
        UpdateSlotSlowdown();   // apply after our own speed is in place
    }

    /// <summary>Leaves Windows with its own pointer settings if we only applied ours temporarily.</summary>
    public void AutoRelease()
    {
        lock (_slowGate) { if (_slowApplied) { _slowApplied = false; NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(_speedBeforeSlow, 1, 20), 0); } }
        if (!_autoApplied) return;
        _autoApplied = false;
        Restore();
    }

    public string Restore()
    {
        if (!B.HasPointerBackup) return "Nothing to restore — no changes have been made.";
        uint f = NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE;
        if (B.MouseParams.Length == 3) NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, (int[])B.MouseParams.Clone(), f);
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)B.PointerSpeed, f);
        if (B.ScrollLinesOriginal >= 0) NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETWHEELSCROLLLINES, (uint)B.ScrollLinesOriginal, IntPtr.Zero, f);
        if (B.DoubleClickOriginal >= 0) NativeMethods.SetDoubleClickTime((uint)B.DoubleClickOriginal);
        B.ScrollLinesOriginal = -1; B.DoubleClickOriginal = -1;
        B.HasPointerBackup = false;
        Svc.Settings.Save();
        return "Original pointer settings restored.";
    }
}

// ============================================================ DNS

public sealed class DnsProvider : ObservableObject
{
    private double? _latency;
    private int _success, _total;
    private string _status = "Not tested";
    private StatusKind _kind = StatusKind.Neutral;
    private bool _best, _current;

    public required string Name { get; init; }
    public required string Primary { get; init; }
    public string Secondary { get; init; } = "";
    public bool IsCustom { get; init; }
    public string Servers => string.IsNullOrEmpty(Secondary) ? Primary : $"{Primary}, {Secondary}";
    public double? LatencyMs { get => _latency; set { if (Set(ref _latency, value)) OnPropertyChanged(nameof(LatencyText)); } }
    public string LatencyText => LatencyMs is double d ? $"{d:0} ms" : "—";
    public int Success { get => _success; set { Set(ref _success, value); OnPropertyChanged(nameof(SuccessText)); } }
    public int Total { get => _total; set { Set(ref _total, value); OnPropertyChanged(nameof(SuccessText)); } }
    public string SuccessText => Total == 0 ? "—" : $"{Success}/{Total}";
    public string Status { get => _status; set => Set(ref _status, value); }
    public StatusKind Kind { get => _kind; set => Set(ref _kind, value); }
    public bool IsBest { get => _best; set => Set(ref _best, value); }
    public bool IsCurrent { get => _current; set => Set(ref _current, value); }
}

public sealed class AdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int InterfaceIndex { get; init; }
    public required List<string> DnsServers { get; init; }
    public bool IsStatic { get; init; }
    public override string ToString() => Name;
}

public sealed class DnsService
{
    private static readonly string[] Domains = { "www.roblox.com", "www.google.com", "www.microsoft.com" };

    public List<DnsProvider> CreateProviders() => new()
    {
        new() { Name = "Cloudflare", Primary = "1.1.1.1", Secondary = "1.0.0.1" },
        new() { Name = "Google Public DNS", Primary = "8.8.8.8", Secondary = "8.8.4.4" },
        new() { Name = "Quad9", Primary = "9.9.9.9", Secondary = "149.112.112.112" },
        new() { Name = "OpenDNS", Primary = "208.67.222.222", Secondary = "208.67.220.220" },
        new() { Name = "AdGuard DNS", Primary = "94.140.14.14", Secondary = "94.140.15.15" },
    };

    public List<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.All(g => g.Address.AddressFamily != AddressFamily.InterNetwork)) continue;
            int idx;
            try { idx = props.GetIPv4Properties().Index; } catch { continue; }
            var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
            list.Add(new AdapterInfo { Id = nic.Id, Name = nic.Name, InterfaceIndex = idx, DnsServers = dns, IsStatic = IsStaticDns(nic.Id) });
        }
        return list;
    }

    private static bool IsStaticDns(string guid)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{guid}");
            return !string.IsNullOrWhiteSpace(k?.GetValue("NameServer") as string);
        }
        catch { return false; }
    }

    /// <summary>Measures real UDP DNS round-trips. The first round is a warm-up and is not counted.</summary>
    public async Task MeasureAsync(DnsProvider p, CancellationToken ct)
    {
        p.Status = "Testing…"; p.Kind = StatusKind.Info;
        var times = new List<double>();
        int attempts = 0;
        for (int round = 0; round < 3; round++)
        {
            foreach (var dom in Domains)
            {
                ct.ThrowIfCancellationRequested();
                var ms = await QueryAsync(p.Primary, dom, ct);
                if (round == 0) continue;
                attempts++;
                if (ms != null) times.Add(ms.Value);
            }
        }
        p.Total = attempts; p.Success = times.Count;
        if (times.Count == 0) { p.LatencyMs = null; p.Status = "No response"; p.Kind = StatusKind.Error; return; }
        times.Sort();
        p.LatencyMs = times[times.Count / 2];
        p.Status = times.Count == attempts ? "Reachable" : "Some packets lost";
        p.Kind = times.Count == attempts ? StatusKind.Success : StatusKind.Warning;
    }

    private static async Task<double?> QueryAsync(string server, string name, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var id = (ushort)Random.Shared.Next(0, 65536);
            var packet = BuildQuery(id, name);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(IPAddress.Parse(server), 53));
            var res = await udp.ReceiveAsync(timeout.Token);
            sw.Stop();
            var b = res.Buffer;
            if (b.Length < 12 || ((b[0] << 8) | b[1]) != id || (b[2] & 0x80) == 0 || (b[3] & 0x0F) != 0 || ((b[6] << 8) | b[7]) == 0) return null;
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static byte[] BuildQuery(ushort id, string name)
    {
        var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        W16(id); W16(0x0100); W16(1); W16(0); W16(0); W16(0);
        foreach (var label in name.Split('.'))
        {
            ms.WriteByte((byte)label.Length);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        ms.WriteByte(0); W16(1); W16(1);
        return ms.ToArray();
    }

    public async Task<(bool Ok, string Message)> ApplyAsync(AdapterInfo adapter, DnsProvider provider)
    {
        // Save the previous configuration first (only if we don't already hold an older one for this adapter).
        var b = Svc.S.Backups;
        if (b.Dns == null || b.Dns.AdapterId != adapter.Id)
        {
            b.Dns = new DnsBackup { AdapterId = adapter.Id, AdapterName = adapter.Name, InterfaceIndex = adapter.InterfaceIndex, WasStatic = adapter.IsStatic, Servers = adapter.DnsServers.ToList() };
            Svc.Settings.Save();
        }
        var wanted = new[] { provider.Primary, provider.Secondary }.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        var script = $"Set-DnsClientServerAddress -InterfaceIndex {adapter.InterfaceIndex} -ServerAddresses ({string.Join(",", wanted.Select(x => $"'{x}'"))}); Clear-DnsClientCache";
        var (ok, msg) = await Elevation.RunPowerShellAsync(script);
        if (!ok) return (false, msg);
        var now = GetAdapters().FirstOrDefault(a => a.Id == adapter.Id);
        bool verified = now != null && now.DnsServers.Take(wanted.Length).SequenceEqual(wanted);
        return verified ? (true, $"DNS on {adapter.Name} is now {provider.Name} ({provider.Servers}).")
                        : (false, "The command finished but Windows still reports different DNS servers.");
    }

    public bool HasBackup => Svc.S.Backups.Dns != null;
    public DnsBackup? Backup => Svc.S.Backups.Dns;

    public async Task<(bool Ok, string Message)> RestoreAsync()
    {
        var d = Svc.S.Backups.Dns;
        if (d == null) return (false, "No saved DNS configuration to restore.");
        var script = d.WasStatic && d.Servers.Count > 0
            ? $"Set-DnsClientServerAddress -InterfaceIndex {d.InterfaceIndex} -ServerAddresses ({string.Join(",", d.Servers.Select(s => $"'{s}'"))}); Clear-DnsClientCache"
            : $"Set-DnsClientServerAddress -InterfaceIndex {d.InterfaceIndex} -ResetServerAddresses; Clear-DnsClientCache";
        var (ok, msg) = await Elevation.RunPowerShellAsync(script);
        if (!ok) return (false, msg);
        Svc.S.Backups.Dns = null;
        Svc.Settings.Save();
        return (true, d.WasStatic ? $"Restored your previous DNS servers on {d.AdapterName}." : $"{d.AdapterName} is back to automatic (DHCP) DNS.");
    }
}

// ============================================================ QoS

public sealed class QosService
{
    public const string PolicyName = "Nighty Roblox";
    public const string AppName = "RobloxPlayerBeta.exe";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\QoS\" + PolicyName;

    public (bool Exists, int? Dscp, string? App) ReadPolicy()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(PolicyKey);
            if (k == null) return (false, null, null);
            int? dscp = int.TryParse(k.GetValue("DSCP Value")?.ToString(), out var d) ? d : null;
            return (true, dscp, k.GetValue("Application Name")?.ToString());
        }
        catch { return (false, null, null); }
    }

    public async Task<(bool Ok, string Message)> ApplyAsync(int dscp)
    {
        var script = $"Get-NetQosPolicy -Name '{PolicyName}' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue; " +
                     $"Get-NetQosPolicy -Name '{PolicyName}' -ErrorAction SilentlyContinue | Remove-NetQosPolicy -Confirm:$false; " +
                     $"New-NetQosPolicy -Name '{PolicyName}' -AppPathNameMatch '{AppName}' -DSCPAction {dscp} -NetworkProfile All | Out-Null";
        var (ok, msg) = await Elevation.RunPowerShellAsync(script);
        if (!ok) return (false, msg);
        var p = ReadPolicy();
        return p.Exists && p.Dscp == dscp ? (true, $"QoS policy created: {AppName} traffic is marked DSCP {dscp}.")
                                          : (false, "The command finished but the policy could not be found afterwards.");
    }

    public async Task<(bool Ok, string Message)> RemoveAsync()
    {
        var script = $"Get-NetQosPolicy -Name '{PolicyName}' -ErrorAction SilentlyContinue | Remove-NetQosPolicy -Confirm:$false";
        var (ok, msg) = await Elevation.RunPowerShellAsync(script);
        if (!ok) return (false, msg);
        return ReadPolicy().Exists ? (false, "The policy is still present.") : (true, "QoS policy removed.");
    }
}
