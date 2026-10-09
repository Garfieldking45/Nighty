using Nighty.Models;

namespace Nighty.Services;

/// <summary>Application service container (simple, explicit singletons).</summary>
public static class Svc
{
    public static SettingsService Settings { get; private set; } = null!;
    public static AppSettings S => Settings.Current;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static RobloxService Roblox { get; private set; } = null!;
    public static SystemUsageService Usage { get; private set; } = null!;
    public static ClickerService Clicker { get; private set; } = null!;
    public static BowSwitchService Bow { get; private set; } = null!;
    public static MacroPlayer Macros { get; private set; } = null!;
    public static CpsMonitor Cps { get; private set; } = null!;
    public static GameModeService GameMode { get; private set; } = null!;
    public static CleanerService Cleaner { get; private set; } = null!;
    public static BrightnessService Brightness { get; private set; } = null!;
    public static MovementService Movement { get; private set; } = null!;
    public static SocdService Socd { get; private set; } = null!;
    public static TweaksService Tweaks { get; private set; } = null!;
    public static PointerService Pointer { get; private set; } = null!;
    public static DnsService Dns { get; private set; } = null!;
    public static QosService Qos { get; private set; } = null!;
    public static PingService Ping { get; private set; } = null!;
    public static FpsService Fps { get; private set; } = null!;
    public static OverlayManager Overlays { get; private set; } = null!;
    public static ModsService Mods { get; private set; } = null!;

    /// <summary>
    /// Windows 11 may throttle a background process (and ignore its timer resolution) while another window such
    /// as Roblox is in front. Nighty must keep its timing, so opt out of both.
    /// </summary>
    private static void DisableBackgroundThrottling()
    {
        var state = new Native.NativeMethods.PROCESS_POWER_THROTTLING_STATE
        {
            Version = 1,
            ControlMask = Native.NativeMethods.THROTTLE_EXECUTION_SPEED | Native.NativeMethods.THROTTLE_IGNORE_TIMER_RESOLUTION,
            StateMask = 0,   // 0 = do NOT throttle / do NOT ignore timer resolution
        };
        Native.NativeMethods.SetProcessInformation(Native.NativeMethods.GetCurrentProcess(), Native.NativeMethods.ProcessPowerThrottling, ref state,
            System.Runtime.InteropServices.Marshal.SizeOf(state));
    }

    public static void Init()
    {
        DisableBackgroundThrottling();
        Settings = new SettingsService();
        Settings.Load();
        Hotkeys = new HotkeyService();
        Roblox = new RobloxService();
        Usage = new SystemUsageService();
        Clicker = new ClickerService(S.Clicker);
        Macros = new MacroPlayer();
        Bow = new BowSwitchService();
        Cps = new CpsMonitor();
        GameMode = new GameModeService();
        Cleaner = new CleanerService();
        Brightness = new BrightnessService();
        Movement = new MovementService();
        Socd = new SocdService(S.Utility.Socd);
        Socd.Sync();
        Tweaks = new TweaksService();
        Pointer = new PointerService();
        Dns = new DnsService();
        Qos = new QosService();
        Ping = new PingService();
        Fps = new FpsService();
        Overlays = new OverlayManager();
        Mods = new ModsService();
        Tweaks.Reapply();
    }

    /// <summary>Stops everything that sends input. Used by the emergency-stop hotkey and on exit.</summary>
    public static void StopAllInput()
    {
        Clicker.Stop();
        Macros.StopAll();
        Bow.Stop();
    }
}
