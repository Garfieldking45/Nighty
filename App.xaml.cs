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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        _single = new Mutex(true, "Nighty.SingleInstance", out bool created);
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

        AppPaths.EnsureCreated();
        Log.Info($"Nighty starting (admin={Elevation.IsAdmin})");
        Svc.Init();
        ThemeService.Apply(Svc.S.General.Theme);
        Svc.GameMode.RecoverFromCrash();
        Svc.Hotkeys.Start();

        string? page = e.Args.FirstOrDefault(a => a.StartsWith("--page="))?[7..];
        var vm = new MainViewModel(page);
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        if (e.Args.Contains("--minimized")) window.WindowState = WindowState.Minimized;
        window.Show();
        Svc.Overlays.Start();
        UpdateService.StartPolling();
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
