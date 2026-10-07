using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace AppleMusicOverlay.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Window _owner;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayIconService(Window owner, Action openSettings, Action exitApplication)
    {
        _owner = owner;
        _openSettings = openSettings;
        _exitApplication = exitApplication;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "MusicFloat",
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => PostToOwner(_openSettings);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("打开设置", null, (_, _) => PostToOwner(_openSettings)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => PostToOwner(_exitApplication)));
        return menu;
    }

    private void PostToOwner(Action action)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished) return;
        _owner.Dispatcher.BeginInvoke(action);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MusicFloat.ico");
            if (File.Exists(iconPath)) return new Icon(iconPath);
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                Icon? icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon != null) return icon;
            }
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }
}
