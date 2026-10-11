using System.Windows.Threading;
using Nighty.Models;

namespace Nighty.Services;

public sealed record Season(string Id, string Name, DateTime StartUtc, DateTime EndUtc, string Greeting, string Emoji, IReadOnlyDictionary<string, string> Steps);

/// <summary>
/// The seasonal themes. Each one is the default theme for everyone while its season runs, then everyone goes back to the
/// theme they had before. Outside its season a seasonal theme can only be used by the signed-in owner.
/// Times are US Eastern.
/// </summary>
public static class Seasons
{
    public const string Halloween = "halloween", Thanksgiving = "thanksgiving", Christmas = "christmas";

    private static DateTime Eastern(int y, int m, int d)
    {
        var local = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
        try { return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")); }
        catch { return DateTime.SpecifyKind(local.AddHours(m == 12 ? 5 : 4), DateTimeKind.Utc); }
    }

    private static IReadOnlyDictionary<string, string> Steps(params string[] six) => new Dictionary<string, string>
    {
        ["Loading your settings"] = six[0], ["Applying your theme"] = six[1], ["Preparing the interface"] = six[2],
        ["Checking Roblox"] = six[3], ["Warming up the clicker"] = six[4], ["Ready"] = six[5],
    };

    public static readonly IReadOnlyList<Season> All = new[]
    {
        new Season(Halloween, "Halloween", DateTime.MinValue, Eastern(2026, 11, 1), "Happy Halloween", char.ConvertFromUtf32(0x1F383),
            Steps("Summoning your settings", "Casting the spooky theme", "Carving the pumpkins", "Haunting Roblox", "Waking the bats", "Boo! Ready")),
        new Season(Thanksgiving, "Thanksgiving", Eastern(2026, 11, 1), Eastern(2026, 12, 1), "Happy Thanksgiving", char.ConvertFromUtf32(0x1F983),
            Steps("Gathering your settings", "Setting the table", "Carving the turkey", "Checking Roblox", "Passing the gravy", "Dinner is served")),
        new Season(Christmas, "Christmas", Eastern(2026, 12, 1), Eastern(2026, 12, 26), "Merry Christmas", char.ConvertFromUtf32(0x1F384),
            Steps("Unwrapping your settings", "Hanging the lights", "Decorating the tree", "Checking Roblox", "Loading the sleigh", "Merry Christmas! Ready")),
    };

    public static Season? Active => All.FirstOrDefault(s => DateTime.UtcNow >= s.StartUtc && DateTime.UtcNow < s.EndUtc);
    public static Season? Find(string? id) => All.FirstOrDefault(s => s.Id == id);
    public static bool IsSeasonTheme(string? id) => Find(id) != null;

    /// <summary>A seasonal theme is usable during its own season, or any time by the owner. Other themes always are.</summary>
    public static bool IsAvailable(string? id) => Find(id) is not { } s || Active?.Id == s.Id || OwnerService.IsOwner;

    public static string StatusOf(Season s)
        => DateTime.UtcNow < s.StartUtc ? "Upcoming" : DateTime.UtcNow < s.EndUtc ? "Running now" : "Ended";

    /// <summary>
    /// First launch in a season: remember the player's own theme and switch to the season's. When no season is running, put
    /// their own theme back (the owner may keep a seasonal theme). Returns true when a setting changed.
    /// </summary>
    public static bool Sync(GeneralSettings g)
    {
        var active = Active;
        if (active != null)
        {
            if (g.SeasonApplied == active.Id)
            {
                // A seasonal theme the player may not use (an old preview, or the owner signed out) gives way to the running season's.
                if (IsSeasonTheme(g.Theme) && !IsAvailable(g.Theme)) { g.Theme = active.Id; return true; }
                return false;
            }
            if (!IsSeasonTheme(g.Theme)) { g.PreSeasonTheme = g.Theme; g.PreSeasonAccent = g.AccentOverride; }
            g.Theme = active.Id;
            g.AccentOverride = null;
            g.SeasonApplied = active.Id;
            return true;
        }

        if (OwnerService.IsOwner && IsSeasonTheme(g.Theme)) return false;
        bool changed = false;
        if (IsSeasonTheme(g.Theme))
        {
            g.Theme = string.IsNullOrEmpty(g.PreSeasonTheme) || IsSeasonTheme(g.PreSeasonTheme) ? "midnight" : g.PreSeasonTheme;
            g.AccentOverride = g.PreSeasonAccent;
            changed = true;
        }
        if (g.SeasonApplied != null || g.PreSeasonTheme != null)
        {
            g.SeasonApplied = null; g.PreSeasonTheme = null; g.PreSeasonAccent = null;
            changed = true;
        }
        return changed;
    }

    private static DispatcherTimer? _watch;
    public static event Action? Rolled;

    /// <summary>Checks once a minute so a season starts and ends on time even if Nighty is left open.</summary>
    public static void StartWatching()
    {
        if (_watch != null) return;
        _watch = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _watch.Tick += (_, _) => Roll();
        _watch.Start();
    }

    /// <summary>Applies any season change now.</summary>
    public static void Roll()
    {
        if (!Sync(Svc.S.General)) return;
        Svc.Settings.Save();
        ThemeService.ApplyFromSettings(true);
        Rolled?.Invoke();
    }
}
