using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Nighty.Services;
using Nighty.ViewModels;
using Nighty.Views;

namespace Nighty;

public partial class App : Application
{
    private Mutex? _single;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The loading screen is the first window, so closing it must not end the app.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _single = new Mutex(true, Environment.GetEnvironmentVariable("NIGHTY_DATA") is { Length: > 0 } ? "Nighty.SingleInstance.Dev" : "Nighty.SingleInstance", out bool created);
        _ownsMutex = created;
        if (!created)
        {
            MessageBox.Show("Nighty is already running.", "Nighty", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

        try { await StartAsync(e); }
        catch (Exception ex)
        {
            Log.Error("Start-up failed", ex);
            MessageBox.Show("Nighty could not start:\n\n" + ex.Message + "\n\nDetails were written to " + AppPaths.Logs, "Nighty", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task StartAsync(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();
        bool minimized = e.Args.Contains("--minimized");
        bool wantSplash = !minimized && !e.Args.Contains("--nosplash") && SplashEnabledInFile();
        string? page = e.Args.FirstOrDefault(a => a.StartsWith("--page="))?[7..];
        var started = Environment.TickCount64;

        SplashWindow? splash = null;
        if (wantSplash)
        {
            splash = new SplashWindow();
            splash.Show();
            await splash.StepAsync("Loading your settings", 0.12, 40);
        }

        Log.Info($"Nighty starting (admin={Elevation.IsAdmin})");
        Svc.Init();

        if (splash != null) await splash.StepAsync("Applying your theme", 0.34);
        ThemeService.ApplyFromSettings();
        ThemeService.ApplyFont(Svc.S.General.FontName);

        if (splash != null) await splash.StepAsync("Preparing the interface", 0.55);
        page ??= Svc.S.General.RememberWindow && Svc.S.General.LastPage.Length > 0 ? Svc.S.General.LastPage : null;
        var vm = new MainViewModel(page);
        var window = new MainWindow { DataContext = vm };
        // Handy for screenshots and support: --page=Settings --tab=3 opens that tab directly.
        if (e.Args.FirstOrDefault(a => a.StartsWith("--tab="))?[6..] is { } tabArg && int.TryParse(tabArg, out int tabNo))
            vm.Current.GetType().GetProperty("Tab")?.SetValue(vm.Current, tabNo);

        if (splash != null) await splash.StepAsync("Checking Roblox", 0.74);
        Svc.GameMode.RecoverFromCrash();
        Svc.Hotkeys.Start();
        Log.Info("Roblox running: " + Svc.Roblox.IsRunning);

        if (splash != null) await splash.StepAsync("Warming up the clicker", 0.9, 0);
        await Task.Run(ClickerService.Prewarm);

        if (splash != null)
        {
            await splash.StepAsync("Ready", 1, 260);
            // Keep the screen up long enough to read, even on a fast PC.
            int left = 1300 - (int)(Environment.TickCount64 - started);
            if (left > 0) await Task.Delay(left);
        }

        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (minimized) window.WindowState = WindowState.Minimized;
        window.Show();
        if (!minimized) window.FadeIn();
        if (splash != null) await splash.CloseAsync();

        Svc.Overlays.Start();
        UpdateService.StartPolling();
        Svc.Discord.Sync();
        if (e.Args.Contains("--selftest-record")) _ = SelfTestRecordAsync();
    }

    /// <summary>Developer check: records a short replay clip and a short recording, reopens both with Windows' player, logs the result, exits.</summary>
    private async Task SelfTestRecordAsync()
    {
        try
        {
            Svc.S.Record.Folder = Path.Combine(AppPaths.Root, "selftest");
            Svc.S.Record.ClipSeconds = 10;
            Svc.Recorder.SetReplay(true);
            await Task.Delay(4500);
            var clip = await Svc.Recorder.SaveClipAsync();
            Log.Info($"SELFTEST clip: ok={clip.Ok} msg={clip.Message} path={clip.Path}");
            Svc.Recorder.SetReplay(false);
            Svc.Recorder.StartRecording();
            await Task.Delay(3000);
            var rec = await Svc.Recorder.StopRecordingAsync();
            Log.Info($"SELFTEST recording: ok={rec.Ok} msg={rec.Message} path={rec.Path}");
            foreach (var p in new[] { clip.Path, rec.Path })
            {
                if (p == null) continue;
                var fi = new FileInfo(p);
                var player = new System.Windows.Media.MediaPlayer();
                var tcs = new TaskCompletionSource<string>();
                player.MediaOpened += (_, _) => tcs.TrySetResult($"opened {player.NaturalVideoWidth}x{player.NaturalVideoHeight}, {player.NaturalDuration}");
                player.MediaFailed += (_, a) => tcs.TrySetResult("FAILED " + a.ErrorException?.Message);
                player.Open(new Uri(p));
                var done = await Task.WhenAny(tcs.Task, Task.Delay(6000));
                Log.Info($"SELFTEST file {fi.Name}: {fi.Length} bytes, {(done == tcs.Task ? tcs.Task.Result : "timeout")}");
                player.Close();
            }
        }
        catch (Exception ex) { Log.Error("SELFTEST failed", ex); }
        Log.Info("SELFTEST done");
        Shutdown();
    }

    /// <summary>Reads the loading-screen preference before the settings are loaded, so the screen can come up first.</summary>
    private static bool SplashEnabledInFile()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return true;
            return !Regex.IsMatch(File.ReadAllText(AppPaths.SettingsFile), "\"ShowSplash\"\\s*:\\s*false");
        }
        catch { return true; }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        e.Handled = true;
        Dialogs.Info("Something went wrong", e.Exception.Message + "\n\nDetails were written to the log folder (Settings → Open logs folder).");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Svc.Settings != null)
            {
                UpdateService.StopPolling();
                Svc.Discord.Shutdown();
                Svc.Recorder.Shutdown();
                Svc.SoftBrightness.RestoreOnExit();
                Svc.StopAllInput();
                Svc.Socd.Stop();
                Svc.Pointer.AutoRelease();
                if (Svc.S.General.RestoreOnClose) Svc.RestoreSystemOnExit();
                Svc.GameMode.RestoreOnExit();     // always give the user their original system settings back
                Svc.Overlays.Shutdown();
                Svc.Toast.Shutdown();
                Svc.Hotkeys.Stop();
                Svc.Settings.Save();
            }
        }
        catch (Exception ex) { Log.Error("Shutdown cleanup failed", ex); }
        if (_ownsMutex) _single?.ReleaseMutex();   // a second instance never owned it
        base.OnExit(e);
    }
}
