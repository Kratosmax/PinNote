using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PinNote.Core.Models;

namespace PinNote.Infrastructure;

internal static class NativeMethods
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int WcaAccentPolicy = 19;
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

    public static void ApplyBackdrop(Window window, Border surface, bool enabled)
    {
        var isWindows11 = Environment.OSVersion.Version.Build >= 22000;
        surface.Opacity = 1;

        if (!enabled)
        {
            surface.Background = new SolidColorBrush(Color.FromRgb(244, 247, 248));
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
        {
            return;
        }

        if (isWindows11)
        {
            var backdrop = 3;
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            var dark = 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            surface.Background = new SolidColorBrush(Color.FromArgb(205, 248, 250, 250));
        }
        else if (TryEnableWindows10Acrylic(hwnd))
        {
            surface.Background = new SolidColorBrush(Color.FromArgb(208, 244, 247, 248));
        }
        else
        {
            surface.Background = new SolidColorBrush(Color.FromRgb(244, 247, 248));
            return;
        }

        window.Background = Brushes.Transparent;
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is { } compositionTarget)
        {
            compositionTarget.BackgroundColor = Colors.Transparent;
        }
    }

    private static bool TryEnableWindows10Acrylic(nint hwnd)
    {
        var accent = new AccentPolicy
        {
            AccentState = 4,
            AccentFlags = 2,
            GradientColor = unchecked((int)0xD0F8F7F4)
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, pointer, fDeleteOld: false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = pointer,
                SizeOfData = size
            };
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
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
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

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
