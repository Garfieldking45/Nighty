# Nighty

Native Windows desktop app (C# / .NET 8 / WPF, MVVM) with pages for Combat, Gaming, Utility, Tweaks, Extras (Bow Switch, hotbar macros, Auto Fish), Mods, Macros, Overlays and Settings.

## Build & run
- Requires the .NET 8 SDK (build only). Run `powershell -ExecutionPolicy Bypass -File build.ps1`
- Output: `publish\Nighty.exe` (self-contained x64; no .NET runtime needed).
- Dev run: `dotnet run -c Release`. Open a page directly with `--page=Gaming`.

## Notes
- Runs unelevated. Windows asks for approval only for DNS apply/restore, QoS policy, and (via "Restart as administrator") the FPS overlay.
- Settings/logs: `%AppData%\Nighty`. Game Mode, pointer, keyboard, DNS and mod changes store originals and can be restored.
- Source layout: `Views/` XAML, `ViewModels/`, `Services/` (system access), `Controls/`, `Resources/` (design tokens + styles), `Native/` (P/Invoke).
