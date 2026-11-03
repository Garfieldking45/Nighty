using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using Nighty.Models;
using Nighty.Mvvm;
using Nighty.Native;

namespace Nighty.Services;

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

/// <summary>
/// Tracking Helper: pointer speed, "Enhance pointer precision", scroll lines and double-click time through SystemParametersInfo.
/// With "Only while Roblox is in front" on, the values switch in the moment Roblox gets focus and the user's own come back the moment it
/// loses it (a foreground-change event, plus a slow safety tick). Those automatic switches are not written to the registry, so a crash can
/// never leave Windows with the tracking values after the next sign-in.
/// </summary>
public sealed class PointerService
{
    private SystemBackups B => Svc.S.Backups;
    public bool HasBackup => B.HasPointerBackup;
    private readonly object _gate = new();
    private bool _autoApplied;

    public (int Speed, bool Precision) ReadCurrent()
    {
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSESPEED, 0, out int speed, 0);
        var mouse = new int[3];
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSE, 0, mouse, 0);
        return (speed, mouse[2] != 0);
    }

    /// <summary>Applies the values and saves them in Windows (the Apply button).</summary>
    public string Apply(int speed, bool precision) => ApplyCore(speed, precision, persist: true);

    /// <summary>
    /// The Apply button. It follows the same rules as everything else: with "only in Roblox" on, nothing is written to Windows here (the values
    /// switch in when Roblox is in front); with the slot-1 slowdown on, the global pointer speed is never touched (only the slow speed is used).
    /// Any earlier permanent apply that is still in force is undone first.
    /// </summary>
    public string ApplySettings()
    {
        var t = Svc.S.Utility.Tracking;
        if (t.ApplyOnlyInRoblox)
        {
            lock (_gate)
            {
                if (B.HasPointerBackup) { ReleaseSlow(); RestoreCore(persist: true); }
                _autoApplied = false;
            }
            AutoTick();
            return Svc.Roblox.IsForeground
                ? "Saved and applied. Windows' own values come back when you leave Roblox. Applied and verified."
                : "Saved. Windows is untouched: these values apply only while Roblox is in front.";
        }
        if (t.SlowInSlotOne)
        {
            int normal;
            lock (_gate) normal = _slowApplied ? _speedBeforeSlow : ReadCurrent().Speed;
            return ApplyCore(normal, t.EnhancePointerPrecision, persist: true);
        }
        return Apply(t.PointerSpeed, t.EnhancePointerPrecision);
    }

    private string ApplyCore(int speed, bool precision, bool persist)
    {
        try
        {
            lock (_gate)
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
                uint f = Flags(persist);
                ApplyScrollAndDoubleClick(f);
                bool ok = NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSE, 0, mouse, f)
                        & NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(speed, 1, 20), f);
                if (!ok) return "Windows rejected the pointer settings.";
                var now = ReadCurrent();
                Rebase(Math.Clamp(speed, 1, 20));
                return now.Speed == Math.Clamp(speed, 1, 20) && now.Precision == precision ? "Applied and verified." : "Applied, but Windows reports different values than requested.";
            }
        }
        catch (Exception ex) { Log.Error("Pointer apply failed", ex); return ex.Message; }
    }

    private static uint Flags(bool persist) => persist ? NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE : NativeMethods.SPIF_SENDCHANGE;

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

    /// <summary>True while the pointer is held at the slow speed.</summary>
    public bool IsSlowed => _slowApplied;

    /// <summary>
    /// The slot-1 slowdown: slow only while Roblox is in front, the Auto Clicker is clicking AND hotbar slot 1 is selected.
    /// In any other slot (or an unknown one), not clicking, or Roblox not in front, the pointer is at its normal speed.
    /// </summary>
    internal static bool WantSlow(bool enabled, bool robloxFront, bool clicking, int slot) => enabled && robloxFront && clicking && slot == 1;

    private static void SetSpeed(int speed) =>
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETMOUSESPEED, 0, (IntPtr)Math.Clamp(speed, 1, 20), NativeMethods.SPIF_SENDCHANGE);

    /// <summary>The normal speed changed while slowed (tracking values applied or restored): remember it and keep the slow speed in place.</summary>
    private void Rebase(int normalSpeed)
    {
        if (!_slowApplied) return;
        _speedBeforeSlow = normalSpeed;
        SetSpeed(Svc.S.Utility.Tracking.SlowSpeed);
    }

    /// <summary>Slows or restores the pointer to match the rule above. Not written to the registry.</summary>
    public void UpdateSlowdown()
    {
        var t = Svc.S.Utility.Tracking;
        bool want = WantSlow(t.SlowInSlotOne, Svc.Roblox.IsForeground, Svc.Clicker.IsClicking, Svc.Bow.CurrentSlot);
        lock (_gate)
        {
            if (want && !_slowApplied)
            {
                _speedBeforeSlow = ReadCurrent().Speed;
                _slowApplied = true;
                SetSpeed(t.SlowSpeed);
            }
            else if (want)
            {
                NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETMOUSESPEED, 0, out int now, 0);
                if (now != Math.Clamp(t.SlowSpeed, 1, 20)) SetSpeed(t.SlowSpeed);
            }
            else if (_slowApplied)
            {
                _slowApplied = false;
                SetSpeed(_speedBeforeSlow);
            }
        }
    }

    /// <summary>Puts the pointer back at its normal speed at once (used before a restore and on exit).</summary>
    private void ReleaseSlow()
    {
        if (!_slowApplied) return;
        _slowApplied = false;
        SetSpeed(_speedBeforeSlow);
    }

    /// <summary>Whether the tracking values should be in force right now: "only in Roblox" is on and Roblox is the window in front.</summary>
    internal static bool WantApplied(bool onlyInRoblox, bool robloxFront) => onlyInRoblox && robloxFront;

    /// <summary>Applies the tracking settings while Roblox is in front and puts Windows' own values back the moment it is not.</summary>
    public void AutoTick()
    {
        var t = Svc.S.Utility.Tracking;
        bool want = WantApplied(t.ApplyOnlyInRoblox, Svc.Roblox.IsForeground);
        lock (_gate)
        {
            if (want && !_autoApplied)
            {
                _autoApplied = true;
                if (t.SlowInSlotOne)
                {
                    // The slot-1 slowdown is on: outside slot 1 + clicking the pointer must be exactly what Windows had, not the tracking speed.
                    var cur = ReadCurrent();
                    bool hadBackup = B.HasPointerBackup;
                    ApplyCore(hadBackup ? B.PointerSpeed : cur.Speed, hadBackup && B.MouseParams.Length == 3 ? B.MouseParams[2] != 0 : cur.Precision, persist: false);
                }
                else ApplyCore(t.PointerSpeed, t.EnhancePointerPrecision, persist: false);
            }
            else if (!want && _autoApplied)
            {
                _autoApplied = false;
                RestoreCore(persist: false);
            }
        }
        UpdateSlowdown();   // after our own speed is in place
    }

    private NativeMethods.WinEventProc? _fgProc;
    private IntPtr _fgHook;

    /// <summary>Switches the moment the foreground window changes instead of waiting for the next tick. Call on the UI thread (the hook needs its message loop).</summary>
    public void StartForegroundWatch()
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc = (_, _, _, _, _, _, _) => { try { AutoTick(); } catch (Exception ex) { Log.Error("Tracking Helper foreground switch failed", ex); } };
        _fgHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_fgHook == IntPtr.Zero) Log.Warn("Tracking Helper could not watch the foreground window; falling back to the timer");
    }

    /// <summary>Leaves Windows with its own pointer settings if we only applied ours temporarily.</summary>
    public void AutoRelease()
    {
        lock (_gate)
        {
            ReleaseSlow();
            if (!_autoApplied) return;
            _autoApplied = false;
            RestoreCore(persist: false);
        }
    }

    /// <summary>Nighty is closing: pointer, scroll and double-click go back to what Windows had, whatever was applied and however.</summary>
    public void RestoreOnExit()
    {
        lock (_gate)
        {
            ReleaseSlow();
            _autoApplied = false;
            if (B.HasPointerBackup) RestoreCore(persist: true);
        }
    }

    public string Restore() { lock (_gate) { ReleaseSlow(); _autoApplied = false; return RestoreCore(persist: true); } }

    private string RestoreCore(bool persist)
    {
        if (!B.HasPointerBackup) return "Nothing to restore — no changes have been made.";
        uint f = Flags(persist);
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

// ============================================================ Network adapters (used by the Ping overlay's adapter list)

public sealed class AdapterInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public override string ToString() => Name;
}

public sealed class NetworkAdapterService
{
    /// <summary>The network adapters that are up and have an IPv4 gateway.</summary>
    public List<AdapterInfo> GetAdapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.All(g => g.Address.AddressFamily != AddressFamily.InterNetwork)) continue;
            list.Add(new AdapterInfo { Id = nic.Id, Name = nic.Name });
        }
        return list;
    }
}
