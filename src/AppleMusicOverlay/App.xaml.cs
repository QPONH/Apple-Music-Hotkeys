using System.Threading;
using System.Windows;

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
        base.OnStartup(e);
        SessionEnding += App_SessionEnding;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow) mainWindow.RequestApplicationExit(isSessionEnding: true);
    }
}
