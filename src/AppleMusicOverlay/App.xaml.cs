using System.Windows;

namespace AppleMusicOverlay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SessionEnding += App_SessionEnding;
    }

    private void App_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.RequestApplicationExit(isSessionEnding: true);
        }
    }
}
