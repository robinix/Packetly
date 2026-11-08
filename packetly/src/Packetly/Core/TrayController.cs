using System.Drawing;
using System.Windows.Forms;

namespace Packetly.Core;

/// <summary>
/// Owns the notification-area icon on a dedicated STA thread with its own
/// WinForms message loop. Menu: open dashboard, pause/resume collection,
/// exit. Disposing stops the loop and removes the icon.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _started = new();
    private readonly Action _openDashboard;
    private readonly Action _exit;
    private readonly Func<string> _togglePause;
    private volatile bool _disposeRequested;

    public TrayController(Action openDashboard, Action exit, Func<string> togglePause)
    {
        _openDashboard = openDashboard;
        _exit = exit;
        _togglePause = togglePause;
        _thread = new Thread(Run) { IsBackground = true, Name = "packetly-tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Task.Wait(5000);
    }

    private void Run()
    {
        using var icon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "Packetly — network monitor",
            Visible = true,
        };

        var pauseItem = new ToolStripMenuItem("Pause collection");
        pauseItem.Click += (_, _) => pauseItem.Text = _togglePause();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => _openDashboard());
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _exit());
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => _openDashboard();

        _started.TrySetResult(true);
        Application.Run(); // message loop for the notify icon

        if (_disposeRequested)
        {
            icon.Visible = false;
        }
    }

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            AppMark.Draw(g, 64);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _disposeRequested = true;
        Application.Exit(); // ends the tray thread's message loop
        _thread.Join(2000);
    }
}
