using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace AppleMusicOverlay.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Window _owner;
    private readonly Action _testOverlay;
    private readonly Action _exitApplication;
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayIconService(Window owner, Action testOverlay, Action exitApplication)
    {
        _owner = owner;
        _testOverlay = testOverlay;
        _exitApplication = exitApplication;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Apple Music Overlay",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => ShowOwner();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => _owner.Dispatcher.Invoke(ShowOwner));
        menu.Items.Add("测试显示", null, (_, _) => _owner.Dispatcher.Invoke(_testOverlay));
        menu.Items.Add("退出", null, (_, _) => _owner.Dispatcher.Invoke(_exitApplication));
        return menu;
    }

    private void ShowOwner()
    {
        _owner.Show();
        _owner.WindowState = WindowState.Normal;
        _owner.Activate();
    }
}
