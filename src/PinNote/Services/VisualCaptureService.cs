using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinNote.Services;

internal static class VisualCaptureService
{
    public static void Capture(FrameworkElement element, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The capture path has no parent directory.");
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    public static void CaptureComposited(Window window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var bounds))
        {
            throw new InvalidOperationException("无法读取测试窗口的屏幕位置。");
        }

        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The capture path has no parent directory.");
        Directory.CreateDirectory(directory);
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(width, height));
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out WindowBounds bounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
