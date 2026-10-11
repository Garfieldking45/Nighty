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
        new("2.0.5", "2026-10-10", new[]
        {
            "Calibrate now aims for 34-35 CPS (it only goes lower if your PC cannot deliver that evenly) and also picks the click duty cycle your PC releases most accurately",
            "New Ms's tab: every millisecond timing in one place - the clicker's time between clicks, button held and released (live from CPS and duty cycle), the crossbow / whim shot timings and the fishing cast hold",
            "JinxyClicker's 12 presets (Measured, Ish, Snoopy and more) are on the Ms's tab: one click sets CPS, duty cycle and mode. Speeds above 100 CPS are limited to 100",
        }),
        new("2.0.4", "2026-10-10", new[]
        {
            "Stop in a screen corner: a new failsafe on the Combat page. Throw the mouse into any corner of the desktop and the Auto Clicker stops (on by default; also stops if you start it while the mouse is already in a corner)",
            "New PC tweak \"Remove Roblox's 60 FPS cap\" on the Gaming page: raises Roblox's frame-rate target using its allowed FastFlag, keeps your other flags, is re-applied after Roblox updates and can be reverted",
        }),
        new("2.0.3", "2026-10-10", new[]
        {
            "Auto Crossbow: the Auto Clicker now pauses only until the shot is released, not through the sword swap, so hit reg stays faster while the crossbow is held",
            "Updates are more reliable: the swap retries while Windows or antivirus still holds the file, Nighty always reopens, and the download is checked for size and checksum first",
        }),
        new("2.0.2", "2026-10-10", new[]
        {
            "Fixed Auto Crossbow shots randomly not firing when the Auto Clicker shares its button: the clicker now holds still during the shot instead of clicking into it",
            "Auto Crossbow no longer adds its own swings while the Auto Clicker is already swinging",
            "Crossbow default cooldown raised to 1400 ms, and a new Cooldown setting (Extras page, 0 = automatic) for shots that are swallowed while the weapon is still reloading",
        }),
        new("2.0.1", "2026-10-10", new[]
        {
            "Auto clicker reliability: Stop now waits until the clicker has really stopped, so no click can arrive after you turn it off and a button can never be left held down",
            "Fixed a hold-mode race where a clicker run that was already finishing could stop the next run you had just started",
            "Fixed letting go of the hold key during the gap between two clicks still sending one more click (most noticeable at low CPS)",
            "Waits now wake the moment the clicker is stopped instead of finishing a sleep of up to 15 ms",
            "Removed HitFix's blocking garbage-collection step at every start, which could stall the UI and delay the first click",
            "Auto Crossbow and Auto Whim: the shot now waits longer for the weapon to equip, is held for at least two frames, and pauses before swapping to the sword, so it fires more reliably. Equip delay, Shot hold and Swap delay are adjustable on the Extras page",
            "Added an automated test suite for the clicker (tests folder)",
        }),

        new("2.0.0", "2026-10-10", new[]
        {
            "New shell: custom top bar (breadcrumb, live Clicker / Roblox / Game Mode status, pin, window buttons), a loading screen that reports each real start-up step, and smooth motion everywhere: animated page changes with cards rising in, hover glow and press squish on buttons, sliding switches, an animated sidebar indicator",
            "8 themes (Midnight, Light, Cyberpunk, Monochrome, Ocean, Forest, Sunset, Sakura) that fade into each other, accent swatches, your own colors for every part of the interface that survive theme changes, a contrast warning, a font picker, a background picture (PNG, JPG or animated GIF), optional sound effects, and an Animations switch",
            "Auto clicker, better hit registration: HitFix (time-critical thread pinned to its own core, no long garbage-collection pauses while clicking, exact final wait before every click), Efficient / Balanced / Precise timing, clicks are checked and you are told when Windows blocks them (a game running as administrator), a timing test that reports measured CPS, jitter, average and 99th-percentile error, and the process CPU it used",
            "Auto stop: stop after a number of clicks or a time limit, and a start delay",
            "Gaming: Calm background apps (browsers, launchers and updaters wait while Roblox runs) and Free up memory, with live counters; everything is put back when Game Mode ends",
            "Utility: software brightness boost and dimming, a mouse test pad (real pointer multiplier, acceleration check, polling rate), and QoS 'use on home Wi-Fi and Ethernet'",
            "Mods: change Roblox's cursor and font (Roblox, Bloxstrap, Fishstrap, Voidstrap and Froststrap; originals are saved and restored; put back after updates), a cursor library with 15 designs, brightness and size, a Cursor Builder with layers, glow, outline and rotation, and turning any picture into a cursor with background removal; export PNG and .cur",
            "Macros: record keys and clicks, duplicate, Hold mode, new steps (key combo, auto-click, double click, button down and up, go to a spot with a spot picker), and share macros as a code, a .nightymacro file or a link",
            "Overlays: ready-made and saved presets, show only while Roblox is in front or open, positions inside the Roblox window, per-overlay colors, opacity, corners, border, shadow and labels, CPS layouts (compact, big, graph) and counting source, frame time, ping colors, space and shift on the WASD overlay, side buttons and CPS on the mouse overlay, key press counter",
            "Record: Instant Replay (the last 10 seconds to 2 minutes, kept in memory only) and full screen or Roblox-window recording to H.264 MP4 using GPU screen capture, a clip library, hotkeys, an on-screen note and sound when a clip is saved. Sound is not recorded yet",
            "Settings: Discord status (your own application id), start where you left off, remember window position, support info, plus the Performance tab",
        }),
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
