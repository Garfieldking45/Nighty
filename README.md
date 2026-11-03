# Nighty

Native Windows desktop app (C# / .NET 8 / WPF, MVVM) with pages for Combat, Gaming, Utility, Mods (Roblox cursor and font, Cursor Builder, picture to cursor), Extras (hotbar macros, Auto Fish), Macros (record, share), Overlays, Record (Instant Replay and screen recording) and Settings (themes, animations, performance).

## Build & run
- Requires the .NET 8 SDK (build only). Run `powershell -ExecutionPolicy Bypass -File build.ps1`
- Output: `publish\Nighty.exe` (self-contained x64; no .NET runtime needed).
- Dev run: `dotnet run -c Release`. Open a page directly with `--page=Gaming`.

## Notes
- Dev options: `--page=Settings --tab=3` opens a page and tab directly, `--nosplash` skips the loading screen, and the `NIGHTY_DATA` environment variable points the app at a separate data folder so a test build never touches your real settings.
- Runs unelevated. Windows asks for approval only for DNS apply/restore, QoS policy, and (via "Restart as administrator") the FPS overlay.
- Settings/logs: `%AppData%\Nighty`. Game Mode, pointer, keyboard, DNS and mod changes store originals and can be restored.
- Source layout: `Views/` XAML, `ViewModels/`, `Services/` (system access), `Controls/`, `Resources/` (design tokens + styles), `Native/` (P/Invoke).

## Gaming: how it works
- **One page, one button.** "Click to boost FPS" (`Services/BoostService.cs`) switches on a short fixed set: dedicated graphics card for Roblox, no 60 FPS cap, maximum-performance power, no Windows throttling, Roblox priority, Windows Game Mode, Game DVR off. The list under the button shows what each did on this PC, or why it was skipped (single GPU, a launcher that rewrites the flag file, already on, ...).
- **Two kinds of change.** The graphics-card choice, FPS flag and Game DVR are read by Roblox at launch, so they are written when the boost is switched on and stay while it is on (and are rewritten when Roblox moves to a new version folder). Power, throttling, priority and Game Mode follow Roblox: applied while it runs, undone when it closes.
- **Everything is journalled.** `Services/Booster/Journal.cs` saves the real original value to `recovery-journal.json` before a change, reads it back to confirm, and restores only a setting that still holds the value Nighty set. Startup recovery undoes anything an earlier run left behind. `GameModeEngine` works on the real Roblox client processes, matched by process id and start time.
- **Deliberately not included:** slowing other programs, trimming memory, a 1 ms timer request, keep-awake, Windows comfort tweaks and cache cleaning. They either did nothing for Roblox or made it worse. The engine and rules for some of them still exist in `Services/Booster/` but nothing switches them on.
- **Tests:** `dotnet test tests/Nighty.Tests -c Release` (journal, engine, rules, boost set, benchmark maths, and read-only live checks of the PC running them).
