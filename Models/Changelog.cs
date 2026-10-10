namespace Nighty.Models;

public sealed record ChangelogEntry(string Version, string Date, string[] Changes)
{
    public string Header => $"{Version}  ·  {Date}";
    public string Body => string.Join("\n", Changes.Select(c => "•  " + c));
}

/// <summary>What changed in each release, newest first. Add a new entry at the top for every version bump.</summary>
public static class Changelog
{
    public static readonly IReadOnlyList<ChangelogEntry> Entries = new ChangelogEntry[]
    {
        new("1.7.0", "2026-10-10", new[]
        {
            "New look matching Lyre: darker palette, rounded cards with icon tiles, accent-bar page titles, a sidebar with version badge, GitHub button and live clicker status",
            "Combat page redone: status card with Start clicking button and live TARGET / MEASURED / DUTY / BUTTON / MODE / CLICKS numbers, big CPS and duty readouts with sliders, Exact / Range switch, and Left / Right / Middle option cards",
            "Settings page now has tabs (Appearance, Startup, Safety, Configuration, Changelog) and accent color swatches that recolour the whole app live",
            "Fixed dropdowns showing raw text such as CrosshairStyleChoice { ... } instead of the option name",
        }),
        new("1.6.2", "2026-10-10", new[]
        {
            "Hotbar macros now follow Nexus's sequences and timings: keys are sent as hardware scancodes, the sword is swung the instant its key goes down, and Lasso places five blocks",
            "Auto Crossbow: block slot (does nothing while your blocks are selected), plus Toggle / Hold / Press modes on every hotbar macro",
            "Hold macros stop the moment you let go of the key",
            "Auto clicker timing reworked like Nexus: clicks per hit share the period, holds are at least 4 ms, a Randomize option varies the gaps, and a stall resyncs instead of bursting",
        }),
        new("1.6.1", "2026-10-10", new[]
        {
            "Auto Crossbow replaces Bow Switch: a hold-key hotbar macro that fires the crossbow, swaps to the sword, clicks through the cooldown and repeats",
        }),
        new("1.6.0", "2026-10-10", new[]
        {
            "Hotbar macros on the Extras tab: Auto Whim, Auto Lasso, Auto Build Up, Auto Melody and Auto GingerBread Man, each with its own key and slots",
            "Macro HUD overlay lists every macro you have switched on with its key and shows which are running",
            "Settings: export and import your whole setup as a plain JSON profile",
            "Settings: UI presets recolour the accent across the app",
            "Auto clicker: Clicks per hit for bursts",
            "Macros: scroll, mouse move, random wait and typed-text steps, repeat delay, speed and Only in Roblox",
            "Overlays: save, load and undo layouts as .nightyoverlay files",
            "Utility: scroll speed and double-click speed, and your own DNS resolvers (up to 6)",
            "Settings: Start minimized (also --minimized)",
        }),
        new("1.5.0", "2026-10-09", new[]
        {
            "Calibrate button on the Combat tab: tests how evenly your PC delivers clicks and sets the highest steady CPS",
            "Auto clicker CPS limit raised from 35 to 50",
            "Bow Switch simplified: timings are built in (1.32 s cooldown) and no delay settings to tune; sword clicks pause during the shot",
            "Bow Switch toggle key: turn the bow switch on or off from anywhere",
            "Notifications in the bottom-right corner, over the game, when you toggle something (can be turned off in Settings)",
            "Game Mode turns on automatically when Roblox starts and restores when it closes",
            "Tracking Helper (Roblox only): your pointer settings apply only while Roblox is in front",
            "Slow down in slot 1: lower pointer speed while hotbar slot 1 is selected",
            "Fixed random clicker lag caused by repeated Roblox focus lookups",
            "Tweaks now find Roblox when launched through Bloxstrap, Fishstrap or Froststrap",
        }),
        new("1.4.2", "2026-10-08", new[]
        {
            "Faster bow switch, block slot, and return to your previous slot",
            "Clicker hook runs on its own thread",
            "Fixed a crash when launching a second instance",
        }),
    };
}
