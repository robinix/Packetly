using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Packetly.Storage;

namespace Packetly.Core;

/// <summary>
/// Extracts crisp 32x32 32-bit ARGB PNG icons for Windows executables using the
/// Windows Shell (SHGetFileInfoW) with a safe ExtractAssociatedIcon fallback.
/// </summary>
internal static class IconExtractor
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const int TargetSize = 32;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(
        string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Extracts an icon PNG from <paramref name="path"/> and persists it to SQLite.
    /// Returns the PNG bytes on success, or null if unavailable.
    /// </summary>
    public static byte[]? TryExtract(Db db, int appId, string? path)
    {
        var png = ExtractPng(path);
        if (png != null)
        {
            try { db.SetAppIcon(appId, png); } catch { }
        }
        return png;
    }

    /// <summary>
    /// Extracts a normalized 32x32 32-bit ARGB PNG for a Windows executable path.
    /// </summary>
    public static byte[]? ExtractPng(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (HostGrouping.IsPseudoPath(path)) return null;
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            if (!File.Exists(path)) return null;

            // Primary: SHGetFileInfoW gives true 32-bit ARGB shell icons with proper alpha
            // across modern Win10/11 apps, Electron apps, and Store/MSIX packages.
            var png = TryExtractViaShell(path);
            if (png != null) return png;

            // Fallback: GDI+ ExtractAssociatedIcon.
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            return IconToPng(icon);
        }
        catch
        {
            // Protected, vanished, or unreadable executable.
            return null;
        }
    }

    private static byte[]? TryExtractViaShell(string path)
    {
        var shfi = new SHFILEINFOW();
        var res = SHGetFileInfoW(
            path,
            0,
            ref shfi,
            (uint)Marshal.SizeOf<SHFILEINFOW>(),
            SHGFI_ICON | SHGFI_LARGEICON);
        if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(shfi.hIcon);
            return IconToPng(icon);
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    private static byte[]? IconToPng(Icon icon)
    {
        using var raw = icon.ToBitmap();
        if (raw.Width <= 0 || raw.Height <= 0) return null;

        using var normalized = new Bitmap(TargetSize, TargetSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(normalized))
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(raw, new Rectangle(0, 0, TargetSize, TargetSize));
        }

        if (!HasVisiblePixels(normalized)) return null;

        using var ms = new MemoryStream();
        normalized.Save(ms, ImageFormat.Png);
        if (ms.Length <= 0 || ms.Length >= 200_000) return null;
        return ms.ToArray();
    }

    private static bool HasVisiblePixels(Bitmap bmp)
    {
        // Sample a 4x4 grid across the 32x32 bitmap to reject completely blank/corrupt bitmaps.
        for (int y = 2; y < bmp.Height; y += 4)
        {
            for (int x = 2; x < bmp.Width; x += 4)
            {
                if (bmp.GetPixel(x, y).A > 8) return true;
            }
        }
        return false;
    }
}
