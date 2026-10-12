namespace Nighty.Services.Booster;

public sealed record RobloxClient(ProcIdentity Id, string? Path, LauncherKind Launcher);

public sealed record RobloxSession(IReadOnlyList<RobloxClient> Clients, LauncherKind Launcher, bool LauncherRunning, IReadOnlyList<string> Notes)
{
    public static readonly RobloxSession None = new(Array.Empty<RobloxClient>(), LauncherKind.Unknown, false, Array.Empty<string>());
    public bool IsRunning => Clients.Count > 0;
}

/// <summary>
/// Finds the Roblox game client that is really running. A launcher such as Bloxstrap starts the official client from its own
/// folder, so the client is recognised by its image name <i>and</i> the folder it runs from, and the launcher is read from that
/// folder (the launcher's own process often exits or is not the parent).
/// </summary>
public static class RobloxClientDetector
{
    public const string ClientName = "RobloxPlayerBeta";
    private static readonly (string Folder, LauncherKind Kind)[] Roots =
    {
        ("Roblox", LauncherKind.Official), ("Bloxstrap", LauncherKind.Bloxstrap), ("Fishstrap", LauncherKind.Fishstrap),
        ("Voidstrap", LauncherKind.Voidstrap), ("Froststrap", LauncherKind.Froststrap),
    };
    private static readonly (string Process, LauncherKind Kind)[] LauncherProcesses =
    {
        ("RobloxPlayerLauncher", LauncherKind.Official), ("Bloxstrap", LauncherKind.Bloxstrap), ("Fishstrap", LauncherKind.Fishstrap),
        ("Voidstrap", LauncherKind.Voidstrap), ("Froststrap", LauncherKind.Froststrap),
    };

    public static LauncherKind LauncherOf(string? imagePath, string localAppData, IEnumerable<string>? extraRoots = null)
    {
        if (string.IsNullOrEmpty(imagePath)) return LauncherKind.Unknown;
        string full = imagePath.Replace('/', '\\');
        foreach (var (folder, kind) in Roots)
        {
            string root = System.IO.Path.Combine(localAppData, folder, "Versions") + "\\";
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return kind;
        }
        foreach (var r in extraRoots ?? Array.Empty<string>())
            if (full.StartsWith(r.TrimEnd('\\') + "\\Versions\\", StringComparison.OrdinalIgnoreCase)) return LauncherKind.Official;
        return LauncherKind.Unknown;
    }

    /// <param name="machineRoots">Folders such as C:\Program Files (x86)\Roblox where an all-users install keeps its Versions folder.</param>
    public static RobloxSession Detect(IProcessSource source, string localAppData, IEnumerable<string>? machineRoots = null)
    {
        var list = source.List();
        var candidates = list.Where(p => string.Equals(p.Name, ClientName, StringComparison.OrdinalIgnoreCase)).ToList();
        var notes = new List<string>();
        var clients = new List<RobloxClient>();
        var roots = machineRoots?.ToList() ?? new List<string>();

        foreach (var c in candidates)
        {
            var d = source.Describe(c.Pid);
            if (d == null) continue;   // exited while we looked, or protected from us
            var kind = LauncherOf(d.Path, localAppData, roots);
            bool fileOk = d.Path != null && string.Equals(System.IO.Path.GetFileName(d.Path), ClientName + ".exe", StringComparison.OrdinalIgnoreCase);
            if (d.Path != null && (!fileOk || kind == LauncherKind.Unknown))
            {
                notes.Add($"A process named {ClientName} (id {c.Pid}) runs from an unexpected folder, so Nighty ignored it.");
                continue;
            }
            if (d.Path == null) notes.Add($"Windows would not tell Nighty where process {c.Pid} runs from; treated as unverified and left alone.");
            else clients.Add(new RobloxClient(d.Id, d.Path, kind));
        }

        // Launcher: trust where the client runs from; otherwise whichever launcher process is alive.
        bool launcherRunning = false;
        LauncherKind running = LauncherKind.Unknown;
        foreach (var (proc, kind) in LauncherProcesses)
            if (list.Any(p => string.Equals(p.Name, proc, StringComparison.OrdinalIgnoreCase))) { launcherRunning = true; if (running == LauncherKind.Unknown || kind != LauncherKind.Official) running = kind; }

        var launcher = clients.Select(c => c.Launcher).FirstOrDefault(k => k != LauncherKind.Unknown);
        if (clients.Count == 0) launcher = LauncherKind.Unknown;
        else if (launcher == LauncherKind.Unknown) launcher = running;
        return new RobloxSession(clients, launcher, launcherRunning, notes);
    }

    public static string Display(LauncherKind k) => k switch
    {
        LauncherKind.Official => "Roblox (official launcher)", LauncherKind.Unknown => "unknown launcher", _ => k.ToString(),
    };
}

/// <summary>Caches the process scan for a moment so several services can ask without repeating it.</summary>
public sealed class RobloxGameLocator : IGameLocator
{
    private readonly IProcessSource _source;
    private readonly string _localAppData;
    private readonly IReadOnlyList<string> _machineRoots;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private RobloxSession? _cached;
    private long _stamp;

    public RobloxGameLocator(IProcessSource source, string? localAppData = null, TimeProvider? time = null, TimeSpan? ttl = null)
    {
        _source = source;
        _localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _time = time ?? TimeProvider.System;
        _ttl = ttl ?? TimeSpan.FromMilliseconds(700);
        _machineRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox"),
        };
    }

    public RobloxSession Find() => Find(false);

    public RobloxSession Find(bool fresh)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (!fresh && _cached != null && _time.GetElapsedTime(_stamp, now) < _ttl) return _cached;
            _cached = RobloxClientDetector.Detect(_source, _localAppData, _machineRoots);
            _stamp = now;
            return _cached;
        }
    }
}

/// <summary>
/// Which other programs Game Mode may ask to wait while the game runs. This is a short allow-list of well-known, safe-to-slow
/// background helpers (browsers, launchers, updaters, sync clients); everything else is left alone. A second, independent
/// deny-list guards against Windows itself, security software, drivers, accessibility tools and Nighty ever being touched.
/// </summary>
public static class BackgroundPolicy
{
    /// <summary>Programs that are slowed (never closed): lower priority and efficiency mode.</summary>
    public static readonly IReadOnlySet<string> Candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "msedgewebview2", "steam", "steamwebhelper", "epicgameslauncher",
        "epicwebhelper", "battle.net", "eadesktop", "ubisoftconnect", "galaxyclient", "onedrive", "dropbox", "googledrivefs", "teams", "ms-teams",
        "microsoftedgeupdate", "googleupdate", "phoneexperiencehost", "widgets", "searchhost", "slack", "skype", "wallpaper32", "wallpaper64",
        "adobeupdateservice", "adobecollabsync", "creativecloud", "ccxprocess", "acrotray", "yourphone", "crossdeviceservice",
        "razercentral", "icue", "lghub_agent", "evernote", "notion", "figma_agent", "overwolf", "overwolfbrowser",
    };

    /// <summary>Never touched, whatever else says otherwise.</summary>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Windows itself
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer", "fontdrvhost", "sihost",
        "ctfmon", "audiodg", "runtimebroker", "taskhostw", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "searchindexer", "taskmgr",
        // security
        "msmpeng", "mssense", "nissrv", "securityhealthservice", "securityhealthsystray", "smartscreen", "sgrmbroker", "mpdefendercoreservice",
        "avp", "ekrn", "egui", "bdagent", "bdservicehost", "mbam", "mbamservice", "mcshield", "masvc", "sophosui", "sophoshealth", "csfalconservice", "csfalconcontainer",
        "sentinelagent", "sentinelhelperservice", "wrsa", "nortonsecurity", "avgsvc", "avguard", "avastsvc", "avastui", "aswengsrv", "avira.servicehost",
        // accessibility
        "narrator", "magnify", "osk", "utilman", "voiceaccess", "atbroker", "livecaptions",
        // graphics, audio and input drivers' helpers
        "nvcontainer", "nvdisplay.container", "nvidia share", "nvidia web helper", "nvtelemetrycontainer", "amdrssrcext", "radeonsoftware", "cnext", "igfxem", "igfxtray",
        "gamingservices", "gamingservicesnet", "xboxpcapp",
        "realtek", "rtkaudiouservice64", "waveseservice", "nahimicservice",
    };

    public static bool IsCandidateName(string? name)
        => !string.IsNullOrEmpty(name) && Candidates.Contains(name) && !IsProtectedName(name);

    public static bool IsProtectedName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (Protected.Contains(name)) return true;
        if (name.Equals("Nighty", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(RobloxClientDetector.ClientName, StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("Roblox", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var l in new[] { "Bloxstrap", "Fishstrap", "Voidstrap", "Froststrap" })
            if (name.Equals(l, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
