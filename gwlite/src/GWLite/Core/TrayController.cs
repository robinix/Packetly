using System.Drawing;
using System.Windows.Forms;

namespace GWLite.Core;

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
        _thread = new Thread(Run) { IsBackground = true, Name = "gwlite-tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Task.Wait(5000);
    }

    private void Run()
    {
        using var icon = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "GWLite — network monitor",
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
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(26, 33, 44));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var ring = new Pen(Color.FromArgb(61, 165, 245), 2.4f);
            g.DrawEllipse(ring, 4, 4, 24, 24);
            using var dot = new SolidBrush(Color.FromArgb(62, 207, 142));
            g.FillEllipse(dot, 12, 12, 8, 8);
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
