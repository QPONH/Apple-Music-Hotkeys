using System.Threading;
using System.Windows;
using System.Windows.Threading;
using AppleMusicOverlay.Services;

namespace AppleMusicOverlay;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\MusicFloatAppleMusicHotkeys_SingleInstance", out bool createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("MusicFloat 已经在运行中，无需重复启动。", "MusicFloat", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        // Install crash reporting before creating the main window. Recoverable UI
        // exceptions are logged and kept from terminating the entire tray app.
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown fatal error"), "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };
        base.OnStartup(e);
        SessionEnding += App_SessionEnding;

        // Create the window by hand instead of StartupUri: a window that has to start hidden must
        // never be shown at all, otherwise it flashes on screen for a frame before hiding to the
        // tray.
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            if (window.PrepareStartup()) window.Show();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Startup:MainWindow");
            MessageBox.Show("程序启动失败：" + ex.Message, "MusicFloat", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception, "DispatcherUnhandledException");
        e.Handled = true;
        try
        {
            MessageBox.Show("程序遇到异常，但已记录日志并尝试继续运行。\n日志位置：" + CrashLog.LogPath + "\n\n" + e.Exception.Message,
                "MusicFloat 运行异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* Do not let error reporting cause a second crash. */ }
    }

    private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow) mainWindow.RequestApplicationExit(isSessionEnding: true);
    }
}
