using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nighty.Services.Booster;

/// <summary>What is configured about Roblox's frame rate and detail level on this PC, read from the files Roblox and its launcher use. Read-only.</summary>
public sealed record FrameRateConfig(
    LauncherKind Launcher,
    string? VersionFolder,
    /// <summary>Flags in the client's own ClientAppSettings.json: the ones the running client actually reads.</summary>
    string? ClientFlagFile,
    IReadOnlyDictionary<string, string> ClientFlags,
    /// <summary>The launcher's own flag list (for example Froststrap's Modifications folder) and whether it rewrites the client's file at each launch.</summary>
    string? LauncherFlagFile,
    IReadOnlyDictionary<string, string> LauncherFlags,
    bool LauncherManagesFlags,
    /// <summary>"Maximum frame rate" from Roblox's own settings file (GlobalBasicSettings).</summary>
    int? FramerateCapSetting,
    int? GraphicsQualityLevel,
    bool? Fullscreen,
    IReadOnlyList<string> LowDetailFlags,
    IReadOnlyList<string> Notes)
{
    public const string TargetFpsFlag = "DFIntTaskSchedulerTargetFps";

    public int? TargetFpsFromFlags => ClientFlags.TryGetValue(TargetFpsFlag, out var v) && int.TryParse(v, out int n) ? n : null;

    /// <summary>The frame-rate limit Roblox will apply, if the files say so; null when no limit is configured in either place.</summary>
    public int? ConfiguredLimit
    {
        get
        {
            int? flag = TargetFpsFromFlags, setting = FramerateCapSetting;
            if (flag is int f && setting is int s) return Math.Min(f, s);
            return flag ?? setting;
        }
    }

    /// <summary>True when the flag Nighty would write exists in the launcher's list, so the launcher keeps it, or it will be rewritten away.</summary>
    public bool LauncherHasTargetFps => LauncherFlags.ContainsKey(TargetFpsFlag);
}

public static class FrameRateInspector
{
    private static readonly (string Name, Func<string, bool> Reduces)[] LowDetail =
    {
        ("FIntFRMMinGrassDistance", v => v == "0"),
        ("FIntFRMMaxGrassDistance", v => v == "0"),
        ("DFIntCSGLevelOfDetailSwitchingDistance", v => v == "0"),
        ("DFIntCSGLevelOfDetailSwitchingDistanceL12", v => v == "0"),
        ("DFIntCSGLevelOfDetailSwitchingDistanceL23", v => v == "0"),
        ("DFIntCSGLevelOfDetailSwitchingDistanceL34", v => v == "0"),
        ("DFFlagDebugPauseVoxelizer", v => v.Equals("true", StringComparison.OrdinalIgnoreCase)),
        ("FFlagDebugSkyGray", v => v.Equals("true", StringComparison.OrdinalIgnoreCase)),
        ("DFIntTextureQualityOverride", v => int.TryParse(v, out int n) && n <= 1),
    };

    private static readonly string[] Launchers = { "Bloxstrap", "Fishstrap", "Voidstrap", "Froststrap" };

    public static FrameRateConfig Inspect(string localAppData, string? versionFolder, LauncherKind launcher)
    {
        var notes = new List<string>();
        versionFolder ??= NewestVersionFolder(localAppData, launcher);
        if (launcher == LauncherKind.Unknown && versionFolder != null) launcher = RobloxClientDetector.LauncherOf(Path.Combine(versionFolder, "RobloxPlayerBeta.exe"), localAppData);

        string? clientFile = versionFolder == null ? null : Path.Combine(versionFolder, "ClientSettings", "ClientAppSettings.json");
        var clientFlags = ReadFlags(clientFile, notes);

        string? launcherFile = null; var launcherFlags = (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(); bool manages = false;
        if (launcher is LauncherKind.Bloxstrap or LauncherKind.Fishstrap or LauncherKind.Voidstrap or LauncherKind.Froststrap)
        {
            string root = Path.Combine(localAppData, launcher.ToString());
            launcherFile = Path.Combine(root, "Modifications", "ClientSettings", "ClientAppSettings.json");
            launcherFlags = ReadFlags(launcherFile, notes);
            manages = launcherFlags.Count > 0 || SettingsSayFlagManager(Path.Combine(root, "Settings.json"));
            if (!File.Exists(launcherFile)) launcherFile = null;
        }

        var (cap, quality, fullscreen) = ReadRobloxSettings(localAppData, notes);
        var lowDetail = LowDetail.Where(l => clientFlags.TryGetValue(l.Name, out var v) && l.Reduces(v)).Select(l => l.Name + "=" + clientFlags[l.Name]).ToList();
        if (clientFlags.TryGetValue("DFFlagTextureQualityOverrideEnabled", out var te) && !te.Equals("true", StringComparison.OrdinalIgnoreCase))
            lowDetail.RemoveAll(x => x.StartsWith("DFIntTextureQualityOverride", StringComparison.Ordinal));

        return new FrameRateConfig(launcher, versionFolder, clientFile != null && File.Exists(clientFile) ? clientFile : null, clientFlags, launcherFile, launcherFlags, manages, cap, quality, fullscreen, lowDetail, notes);
    }

    private static string? NewestVersionFolder(string local, LauncherKind prefer)
    {
        var order = new List<string>();
        if (prefer is not (LauncherKind.Unknown or LauncherKind.Official)) order.Add(prefer.ToString());
        order.AddRange(Launchers.Append("Roblox").Where(l => !order.Contains(l)));
        return order.Select(r => Path.Combine(local, r, "Versions")).Where(Directory.Exists)
            .SelectMany(r => new DirectoryInfo(r).GetDirectories("version-*"))
            .Where(d => File.Exists(Path.Combine(d.FullName, "RobloxPlayerBeta.exe")))
            .OrderByDescending(d => d.LastWriteTimeUtc).Select(d => d.FullName).FirstOrDefault();
    }

    private static IReadOnlyDictionary<string, string> ReadFlags(string? path, List<string> notes)
    {
        if (path == null || !File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            return d;
        }
        catch (Exception ex) { notes.Add($"Could not read {Path.GetFileName(path)}: {ex.Message}"); return new Dictionary<string, string>(); }
    }

    private static bool SettingsSayFlagManager(string settingsJson)
    {
        try { return File.Exists(settingsJson) && Regex.IsMatch(File.ReadAllText(settingsJson), "\"UseFastFlagManager\"\\s*:\\s*true", RegexOptions.IgnoreCase); }
        catch { return false; }
    }

    private static (int? Cap, int? Quality, bool? Fullscreen) ReadRobloxSettings(string local, List<string> notes)
    {
        try
        {
            string dir = Path.Combine(local, "Roblox");
            if (!Directory.Exists(dir)) return (null, null, null);
            var file = new DirectoryInfo(dir).GetFiles("GlobalBasicSettings_*.xml").Where(f => !f.Name.Contains("Studio", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file == null) return (null, null, null);
            string xml = File.ReadAllText(file.FullName);
            int? Int(string name) => Regex.Match(xml, $"<int name=\"{name}\">(-?\\d+)</int>") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
            bool? fs = Regex.Match(xml, "<bool name=\"Fullscreen\">(true|false)</bool>") is { Success: true } b ? b.Groups[1].Value == "true" : null;
            return (Int("FramerateCap"), Int("GraphicsQualityLevel"), fs);
        }
        catch (Exception ex) { notes.Add("Could not read Roblox's settings file: " + ex.Message); return (null, null, null); }
    }
}
