namespace Packetly.Core;

/// <summary>Draws the Packetly app mark: dark rounded tile, bold violet "P",
/// pastel-blue packet chip. Shared by the tray icon and the runtime .ico writer.</summary>
public static class AppMark
{
    public static void Draw(System.Drawing.Graphics g, int size)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var bg = Color.FromArgb(14, 17, 22);
        var border = Color.FromArgb(32, 39, 48);
        var violet = Color.FromArgb(142, 106, 238);
        var blue = Color.FromArgb(156, 200, 247);
        using var bgb = new SolidBrush(bg);
        using var borderp = new Pen(border, Math.Max(1.5f, size * 0.012f));
        var r = (int)(size * 0.22);
        var rect = new Rectangle(1, 1, size - 2, size - 2);
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = r * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(bgb, path);
        g.DrawPath(borderp, path);
        using var pf = new Font("Segoe UI", size * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var violetB = new SolidBrush(violet);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var pr = new Rectangle(0, size * 5 / 100, size, size);
        g.DrawString("P", pf, violetB, pr, fmt);
        int cs = Math.Max(6, size * 19 / 100);
        int cx = size * 68 / 100, cy = size * 26 / 100;
        using var blueB = new SolidBrush(blue);
        g.FillEllipse(blueB, cx - cs / 2, cy - cs / 2, cs, cs);
    }
}
