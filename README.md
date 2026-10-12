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
- **Discover, analyse, recommend, approve, apply, verify, restore.** `Services/Booster/` reads the PC (`HardwareDiscovery`), samples it live (`Telemetry`), explains what it sees (`Analysis`) and proposes actions with evidence (`Recommendations`). Nothing is changed until the plan is approved on Gaming > Plan.
- **Every change is journalled.** `Journal.cs` saves the real original value to `recovery-journal.json` (in the data folder) before a change, reads it back to confirm, and restores only a setting that still holds the value Nighty set. Startup recovery undoes anything an earlier run left behind. `GameModeEngine` applies approved actions to the real Roblox client processes, matched by process id and start time.
- **Measurements are labelled.** Unavailable readings (for example processor temperature) show as unavailable, never as a guess. GPU temperature, clocks and power come from the NVIDIA driver library when it is present.
- **Benchmarks** capture per-frame Present timing (needs administrator rights or the "Performance Log Users" group), keep every run in `benchmarks.json`, and compare groups of runs with a statistical test. `Benchmark.cs` documents the metric definitions.
- **Tests:** `dotnet test tests/Nighty.Tests -c Release` (journal, engine, rules, analysis, benchmark maths, and read-only live checks of the PC running them).
