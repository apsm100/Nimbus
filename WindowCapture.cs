using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static VmMonitor.NativeMethods;

namespace VmMonitor;

/// <summary>
/// Lightweight description of a top-level window discovered on the desktop.
/// </summary>
public readonly record struct WindowInfo(IntPtr Handle, string Title, uint ProcessId, string ProcessName);

/// <summary>
/// Enumerates candidate windows and captures their pixels into WPF bitmaps.
/// </summary>
public static class WindowCapture
{
    // ---- GDI interop kept local to the capture implementation ----
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>
    /// Returns every visible, titled, top-level window. When <paramref name="titleFilter"/>
    /// is supplied, only windows whose title or process name contain it (case-insensitive)
    /// are returned.
    /// </summary>
    public static List<WindowInfo> Enumerate(string? titleFilter, IntPtr selfHandle)
    {
        var results = new List<WindowInfo>();
        var filter = string.IsNullOrWhiteSpace(titleFilter) ? null : titleFilter.Trim();

        EnumWindows((hWnd, _) =>
        {
            if (hWnd == selfHandle) return true;
            if (!IsWindowVisible(hWnd)) return true;

            // Skip tool windows (tray helpers, tooltips, etc.).
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            string processName = GetProcessName(pid);

            if (filter is not null &&
                title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                processName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return true;
            }

            results.Add(new WindowInfo(hWnd, title, pid, processName));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Captures the pixels of <paramref name="hWnd"/> as a frozen WPF bitmap, or
    /// null if the window is minimized / has no drawable area.
    /// </summary>
    public static BitmapSource? Capture(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) return null;
        if (!GetWindowRect(hWnd, out RECT rect)) return null;

        int width = rect.Width;
        int height = rect.Height;
        if (width <= 0 || height <= 0) return null;

        IntPtr windowDc = GetWindowDC(hWnd);
        if (windowDc == IntPtr.Zero) return null;

        IntPtr memDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(windowDc);
            bitmap = CreateCompatibleBitmap(windowDc, width, height);
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero) return null;

            oldBitmap = SelectObject(memDc, bitmap);

            // PW_RENDERFULLCONTENT captures composited/remote surfaces correctly.
            if (!PrintWindow(hWnd, memDc, PW_RENDERFULLCONTENT))
                return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(hWnd, windowDc);
        }
    }

    /// <summary>
    /// Produces a cheap, downscaled grayscale signature of a frame so consecutive
    /// captures can be compared to decide whether the window is changing (Active)
    /// or static (Idle).
    /// </summary>
    public static long ComputeFrameHash(BitmapSource source)
    {
        const int size = 16;
        var scaled = new TransformedBitmap(
            source,
            new ScaleTransform((double)size / source.PixelWidth, (double)size / source.PixelHeight));
        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

        int stride = size;
        var pixels = new byte[stride * size];
        gray.CopyPixels(pixels, stride, 0);

        // FNV-1a over the downscaled bytes.
        long hash = unchecked((long)1469598103934665603UL);
        foreach (byte b in pixels)
        {
            hash ^= b;
            hash *= 1099511628211L;
        }
        return hash;
    }
}
