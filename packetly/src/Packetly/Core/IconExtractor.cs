using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Packetly.Storage;

namespace Packetly.Core;

/// <summary>
/// Extracts crisp 32x32 32-bit ARGB PNG icons for Windows executables.
/// Prefers thread-safe PE resource extraction (PrivateExtractIconsW / ExtractIconExW)
/// over COM-dependent shell queries so thread-pool extraction never intermittently
/// fails or falls back to generic placeholder icons, and renders HICONs via DrawIconEx
/// to avoid GDI+ Icon.ToBitmap() 32bpp alpha corruption.
/// </summary>
internal static class IconExtractor
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint DI_NORMAL = 0x0003;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIconsW(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        [Out] IntPtr[] phicon,
        [Out] uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(
        string lpszFile,
        int nIconIndex,
        [Out] IntPtr[]? phiconLarge,
        [Out] IntPtr[]? phiconSmall,
        uint nIcons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(
        string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(
        IntPtr hdc,
        int xLeft,
        int yTop,
        IntPtr hIcon,
        int cxWidth,
        int cyWidth,
        uint istepIfAniCur,
        IntPtr hbrFlickerFreeDraw,
        uint diFlags);

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

            // 1. PrivateExtractIconsW: reads the exact 32x32 icon directly from PE
            //    resources, MTA thread-safe, handles Vista+ PNG-compressed icons cleanly.
            var png = TryExtractPrivate(path);
            if (png != null) return png;

            // 2. ExtractIconExW: MTA thread-safe Win32 PE icon resource fallback.
            png = TryExtractIconEx(path);
            if (png != null) return png;

            // 3. SHGetFileInfoW: handles MSIX / UWP / shell-associated apps whose
            //    icon lives outside standard Win32 PE resources.
            png = TryExtractViaShell(path);
            if (png != null) return png;

            // 4. Final fallback: GDI+ ExtractAssociatedIcon.
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

    private static byte[]? TryExtractPrivate(string path)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        var count = PrivateExtractIconsW(path, 0, TargetSize, TargetSize, handles, ids, 1, 0);
        if (count == 0 || count == uint.MaxValue || handles[0] == IntPtr.Zero) return null;
        try
        {
            return HIconToPng(handles[0]);
        }
        finally
        {
            DestroyIcon(handles[0]);
        }
    }

    private static byte[]? TryExtractIconEx(string path)
    {
        var large = new IntPtr[1];
        var count = ExtractIconExW(path, 0, large, null, 1);
        if (count == 0 || large[0] == IntPtr.Zero) return null;
        try
        {
            return HIconToPng(large[0]);
        }
        finally
        {
            DestroyIcon(large[0]);
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
            return HIconToPng(shfi.hIcon);
        }
        finally
        {
            DestroyIcon(shfi.hIcon);
        }
    }

    private static byte[]? HIconToPng(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;

        // Primary: DrawIconEx renders 32-bit ARGB / PNG-compressed icons cleanly
        // without System.Drawing.Icon.ToBitmap()'s alpha corruption quirks.
        using (var bmp = new Bitmap(TargetSize, TargetSize, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                var hdc = g.GetHdc();
                try
                {
                    DrawIconEx(hdc, 0, 0, hIcon, TargetSize, TargetSize, 0, IntPtr.Zero, DI_NORMAL);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            if (HasVisiblePixels(bmp))
            {
                var png = EncodePng(bmp);
                if (png != null) return png;
            }
        }

        // Fallback: managed Icon.FromHandle -> ToBitmap.
        using var icon = Icon.FromHandle(hIcon);
        return IconToPng(icon);
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
        return EncodePng(normalized);
    }

    private static byte[]? EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        if (ms.Length <= 0 || ms.Length >= 200_000) return null;
        return ms.ToArray();
    }

    private static bool HasVisiblePixels(Bitmap bmp)
    {
        // Sample across the 32x32 bitmap to reject completely blank/corrupt bitmaps.
        for (int y = 2; y < bmp.Height; y += 3)
        {
            for (int x = 2; x < bmp.Width; x += 3)
            {
                var px = bmp.GetPixel(x, y);
                if (px.A > 8 || px.R > 8 || px.G > 8 || px.B > 8) return true;
            }
        }
        return false;
    }
}
