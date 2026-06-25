using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace AppleMusicOverlay.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Window _owner;
    private readonly Action _showCurrentTrack;
    private readonly Action _exitApplication;
    private readonly LocalizationService _localizer;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _disposed;
    private Forms.ToolStripMenuItem? _openItem;
    private Forms.ToolStripMenuItem? _showOverlayItem;
    private Forms.ToolStripMenuItem? _exitItem;

    public TrayIconService(
        Window owner,
        Action showCurrentTrack,
        Action exitApplication,
        LocalizationService? localizer = null)
    {
        _owner = owner;
        _showCurrentTrack = showCurrentTrack;
        _exitApplication = exitApplication;
        _localizer = localizer ?? LocalizationService.Current;
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "MusicFloat",
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _notifyIcon.DoubleClick += (_, _) => PostToOwner(ShowOwner);
    }

    public void UpdateText()
    {
        if (_disposed)
        {
            return;
        }

        if (_openItem != null)
        {
            _openItem.Text = _localizer.Text("TrayOpen");
        }

        if (_showOverlayItem != null)
        {
            _showOverlayItem.Text = _localizer.Text("TrayShowOverlay");
        }

        if (_exitItem != null)
        {
            _exitItem.Text = _localizer.Text("TrayExit");
        }
    }

    public void PrepareForExit()
    {
        if (_disposed)
        {
            return;
        }

        if (_exitItem != null)
        {
            _exitItem.Enabled = false;
        }

        _notifyIcon.Visible = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        _openItem = new Forms.ToolStripMenuItem(_localizer.Text("TrayOpen"), null, (_, _) => PostToOwner(ShowOwner));
        _showOverlayItem = new Forms.ToolStripMenuItem(_localizer.Text("TrayShowOverlay"), null, (_, _) => PostToOwner(_showCurrentTrack));
        menu.Items.Add(_openItem);
        menu.Items.Add(_showOverlayItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _exitItem = new Forms.ToolStripMenuItem(_localizer.Text("TrayExit"), null, (_, _) => PostToOwner(_exitApplication));
        menu.Items.Add(_exitItem);
        return menu;
    }

    private void PostToOwner(Action action)
    {
        if (_disposed || _owner.Dispatcher.HasShutdownStarted || _owner.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _owner.Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void ShowOwner()
    {
        if (_disposed)
        {
            return;
        }

        _owner.Show();
        _owner.WindowState = WindowState.Normal;
        _owner.Activate();
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MusicFloat.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }

            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                Icon? associatedIcon = Icon.ExtractAssociatedIcon(processPath);
                if (associatedIcon != null)
                {
                    return associatedIcon;
                }
            }

            return (Icon)SystemIcons.Application.Clone();
        }
        catch
        {
            return (Icon)SystemIcons.Application.Clone();
        }
    }
}
