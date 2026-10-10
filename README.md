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
