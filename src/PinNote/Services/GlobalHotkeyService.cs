using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using PinNote.Core.Models;

namespace PinNote.Services;

internal sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int NewNoteHotkeyId = 0x4101;
    private const int ManagerHotkeyId = 0x4102;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly Action _createNote;
    private readonly Action _showManager;
    private AppSettings? _registeredSettings;
    private bool _newNoteRegistered;
    private bool _managerRegistered;
    private bool _disposed;

    public GlobalHotkeyService(Action createNote, Action showManager)
    {
        _createNote = createNote;
        _showManager = showManager;
        var parameters = new HwndSourceParameters("PinNote.GlobalHotkeys")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public string? TryApply(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);

        if (!TryValidate(settings, out var newNote, out var manager, out var validationError))
        {
            return validationError;
        }

        var previous = _registeredSettings?.Clone();
        UnregisterAll();
        if (!TryRegister(NewNoteHotkeyId, settings.NewNoteHotkeyEnabled, newNote, "新建便签", out var error) ||
            !TryRegister(ManagerHotkeyId, settings.ManagerHotkeyEnabled, manager, "管理页面", out error))
        {
            UnregisterAll();
            if (previous is not null && TryValidate(previous, out var oldNew, out var oldManager, out _))
            {
                _ = TryRegister(NewNoteHotkeyId, previous.NewNoteHotkeyEnabled, oldNew, "新建便签", out _);
                _ = TryRegister(ManagerHotkeyId, previous.ManagerHotkeyEnabled, oldManager, "管理页面", out _);
                _registeredSettings = previous;
            }
            return error;
        }

        _registeredSettings = settings.Clone();
        return null;
    }

    public static bool TryNormalize(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var converter = new KeyGestureConverter();
            if (converter.ConvertFromInvariantString(value.Replace(" ", string.Empty)) is not KeyGesture gesture ||
                !IsSupported(gesture))
            {
                return false;
            }
            normalized = converter.ConvertToInvariantString(gesture) ?? string.Empty;
            return normalized.Length > 0;
        }
        catch (Exception exception) when (exception is NotSupportedException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    public static bool TryFromKeyEvent(KeyEventArgs e, out string normalized)
    {
        normalized = string.Empty;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return false;
        }

        var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt);
        if (modifiers == ModifierKeys.None || key is Key.None or Key.Escape or Key.Tab)
        {
            return false;
        }

        var gesture = new KeyGesture(key, modifiers);
        if (!IsSupported(gesture))
        {
            return false;
        }
        normalized = new KeyGestureConverter().ConvertToInvariantString(gesture) ?? string.Empty;
        return normalized.Length > 0;
    }

    private static bool TryValidate(
        AppSettings settings,
        out KeyGesture? newNote,
        out KeyGesture? manager,
        out string? error)
    {
        newNote = null;
        manager = null;
        error = null;

        if (settings.NewNoteHotkeyEnabled && !TryParse(settings.NewNoteHotkey, out newNote))
        {
            error = "新建便签快捷键无效。请至少使用 Ctrl、Shift 或 Alt 中的一个修饰键。";
            return false;
        }
        if (settings.ManagerHotkeyEnabled && !TryParse(settings.ManagerHotkey, out manager))
        {
            error = "管理页面快捷键无效。请至少使用 Ctrl、Shift 或 Alt 中的一个修饰键。";
            return false;
        }
        if (settings.NewNoteHotkeyEnabled && settings.ManagerHotkeyEnabled && newNote is not null && manager is not null &&
            newNote.Key == manager.Key && newNote.Modifiers == manager.Modifiers)
        {
            error = "两个功能不能使用同一个快捷键。";
            return false;
        }
        return true;
    }

    private static bool TryParse(string value, out KeyGesture? gesture)
    {
        gesture = null;
        if (!TryNormalize(value, out var normalized))
        {
            return false;
        }
        gesture = new KeyGestureConverter().ConvertFromInvariantString(normalized) as KeyGesture;
        return gesture is not null;
    }

    private bool TryRegister(int id, bool enabled, KeyGesture? gesture, string label, out string? error)
    {
        error = null;
        if (!enabled)
        {
            return true;
        }
        if (gesture is null || !RegisterHotKey(_source.Handle, id, ToNativeModifiers(gesture.Modifiers) | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(gesture.Key)))
        {
            error = $"{label}快捷键已被其他程序占用，请换一个组合。";
            return false;
        }
        if (id == NewNoteHotkeyId) _newNoteRegistered = true;
        if (id == ManagerHotkeyId) _managerRegistered = true;
        return true;
    }

    private static bool IsSupported(KeyGesture gesture) =>
        gesture.Modifiers != ModifierKeys.None &&
        (gesture.Modifiers & ModifierKeys.Windows) == 0 &&
        KeyInterop.VirtualKeyFromKey(gesture.Key) > 0;

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        var result = 0u;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= ModShift;
        return result;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey)
        {
            return 0;
        }

        handled = true;
        switch (wParam.ToInt32())
        {
            case NewNoteHotkeyId when _newNoteRegistered:
                _createNote();
                break;
            case ManagerHotkeyId when _managerRegistered:
                _showManager();
                break;
        }
        return 0;
    }

    private void UnregisterAll()
    {
        _ = UnregisterHotKey(_source.Handle, NewNoteHotkeyId);
        _ = UnregisterHotKey(_source.Handle, ManagerHotkeyId);
        _newNoteRegistered = false;
        _managerRegistered = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
