using Microsoft.Win32;

namespace Nighty.Services.Booster;

public interface ISystemStateReader
{
    SystemSnapshot Read(RobloxSession game, IReadOnlySet<string> tweaksApplied, IReadOnlySet<string> sessionEnabled);
}

/// <summary>Reads the current Windows and Roblox settings the rules care about. Read-only.</summary>
public sealed class WindowsSystemStateReader : ISystemStateReader
{
    private readonly string _localAppData;
    public WindowsSystemStateReader(string? localAppData = null)
        => _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public SystemSnapshot Read(RobloxSession game, IReadOnlySet<string> tweaksApplied, IReadOnlySet<string> sessionEnabled)
    {
        bool? dvr = null;
        try
        {
            using var a = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore");
            using var b = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            int? v1 = a?.GetValue("GameDVR_Enabled") as int?, v2 = b?.GetValue("AppCaptureEnabled") as int?;
            if (v1 != null || v2 != null) dvr = v1 == 1 || v2 == 1;
            else dvr = null;
        }
        catch { }

        bool? gameMode = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            // Windows' default when nothing is stored is on.
            gameMode = k?.GetValue("AutoGameModeEnabled") is int i ? i != 0 : (k?.GetValue("AllowAutoGameMode") is int j ? j != 0 : true);
        }
        catch { }

        var cfg = FrameRateInspector.Inspect(_localAppData, game.Clients.Select(c => c.Path).Where(p => p != null).Select(p => Path.GetDirectoryName(p)).FirstOrDefault(), game.Launcher);
        string? exe = game.Clients.Select(c => c.Path).FirstOrDefault(p => p != null) ?? (cfg.VersionFolder == null ? null : Path.Combine(cfg.VersionFolder, "RobloxPlayerBeta.exe"));
        string? pref = null;
        try
        {
            if (exe != null) { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"); pref = k?.GetValue(exe) as string; }
        }
        catch { }
        return new SystemSnapshot(dvr, gameMode, exe, pref, cfg, tweaksApplied, sessionEnabled, game);
    }
}
