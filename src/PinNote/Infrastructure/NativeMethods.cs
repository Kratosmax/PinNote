using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using PinNote.Core.Models;

namespace PinNote.Infrastructure;

internal static class NativeMethods
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int SwShownoactivate = 4;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNoactivate = 0x0010;
    private const uint FlashwTray = 0x00000002;
    private const uint FlashwTimernoFG = 0x0000000C;
    private const int WmTimeChange = 0x001E;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmResumeCritical = 0x0006;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotopmost = new(-2);
    private static readonly uint ShowExistingMessage = RegisterWindowMessage("PinNote.ShowExisting.v1");
    private static long _lastSystemRefreshTick;

    public static uint RegisteredShowExistingMessage => ShowExistingMessage;

    public static void BroadcastShowExisting() => PostMessage(new nint(0xffff), ShowExistingMessage, 0, 0);

    public static BackdropResult ApplyBackdrop(Window window, Border surface, bool enabled, byte surfaceTintAlpha = 190)
    {
        surface.Opacity = 1;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
        {
            ApplyOpaqueFallback(window, surface);
            return new BackdropResult(false, null, null);
        }

        if (!enabled || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            || AppContext.TryGetSwitch("PinNote.DisableBackdrop", out var disabled) && disabled)
        {
            DisableSystemBackdrop(hwnd);
            ApplyOpaqueFallback(window, surface);
            return new BackdropResult(false, null, null);
        }

        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: not null } source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        var rounded = 2;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref rounded, sizeof(int));
        var backdrop = 3;
        var backdropResult = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        var fullWindow = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var frameResult = DwmExtendFrameIntoClientArea(hwnd, ref fullWindow);
        if (backdropResult != 0 || frameResult != 0)
        {
            DisableSystemBackdrop(hwnd);
            ApplyOpaqueFallback(window, surface);
            return new BackdropResult(false, backdropResult, frameResult);
        }

        var dark = 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        window.Background = Brushes.Transparent;
        surface.Background = new SolidColorBrush(Color.FromArgb(surfaceTintAlpha, 248, 250, 250));
        surface.CornerRadius = new CornerRadius(0);
        surface.ClipToBounds = false;
        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.CornerRadius = new CornerRadius(0);
        }
        return new BackdropResult(true, backdropResult, frameResult);
    }

    private static void ApplyOpaqueFallback(Window window, Border surface)
    {
        var fallback = Color.FromRgb(244, 247, 248);
        window.Background = new SolidColorBrush(fallback);
        surface.Background = new SolidColorBrush(fallback);
        surface.CornerRadius = new CornerRadius(8);
        surface.ClipToBounds = true;
        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.CornerRadius = new CornerRadius(8);
        }
    }

    private static void DisableSystemBackdrop(nint hwnd)
    {
        var none = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
        var resetFrame = new Margins();
        _ = DwmExtendFrameIntoClientArea(hwnd, ref resetFrame);
    }

    public static void ShowWithoutActivation(Window window, PinMode restoreMode)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        ShowWindow(hwnd, SwShownoactivate);
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);

        if (restoreMode == PinMode.Desktop)
        {
            _ = window.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                SetWindowPos(hwnd, HwndNotopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
            });
        }
    }

    public static void TryActivate(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Topmost = true;
        window.Activate();
        var hwnd = new WindowInteropHelper(window).Handle;
        SetForegroundWindow(hwnd);
    }

    public static void FlashTaskbar(Window window, uint count = 5)
    {
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Hwnd = new WindowInteropHelper(window).Handle,
            Flags = FlashwTray | FlashwTimernoFG,
            Count = count,
            Timeout = 0
        };
        FlashWindowEx(ref info);
    }

    public static void InstallMessageHook(Window window, Action showExisting, Action systemClockChanged)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new Win32Exception("Could not obtain the window source.");
        source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if ((uint)message == ShowExistingMessage)
            {
                handled = true;
                showExisting();
            }
            else if (message == WmTimeChange ||
                     (message == WmPowerBroadcast && (wParam.ToInt32() is PbtApmResumeCritical or PbtApmResumeSuspend or PbtApmResumeAutomatic)))
            {
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastSystemRefreshTick) > 500)
                {
                    Interlocked.Exchange(ref _lastSystemRefreshTick, now);
                    systemClockChanged();
                }
            }

            return 0;
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}

internal sealed record BackdropResult(bool Applied, int? BackdropHResult, int? FrameHResult);
