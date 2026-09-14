using System.Drawing;
using Forms = System.Windows.Forms;

namespace VrcPicSorter.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;

    public TrayService(Action showWindow, Func<Task> scan, Func<Exception, Task> scanFailed, Action exit)
    {
        ArgumentNullException.ThrowIfNull(showWindow);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(scanFailed);
        ArgumentNullException.ThrowIfNull(exit);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open VRC Pic Sorter", null, (_, _) => showWindow());
        menu.Items.Add("Scan now", null, (_, _) => _ = AsyncCommandRunner.RunAsync(scan, scanFailed));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        var executableIcon = Environment.ProcessPath is { } processPath
            ? Icon.ExtractAssociatedIcon(processPath)
            : null;
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = executableIcon ?? SystemIcons.Application,
            Text = "VRC Pic Sorter",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    public void ShowNotification(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(3500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
