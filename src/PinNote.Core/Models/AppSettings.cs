namespace PinNote.Core.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    public bool EnableMaterial { get; set; } = true;

    public bool NewNoteHotkeyEnabled { get; set; } = true;

    public string NewNoteHotkey { get; set; } = "Ctrl+Shift+N";

    public bool ManagerHotkeyEnabled { get; set; } = true;

    public string ManagerHotkey { get; set; } = "Ctrl+Shift+B";

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        EnableMaterial = EnableMaterial,
        NewNoteHotkeyEnabled = NewNoteHotkeyEnabled,
        NewNoteHotkey = NewNoteHotkey,
        ManagerHotkeyEnabled = ManagerHotkeyEnabled,
        ManagerHotkey = ManagerHotkey
    };

    public void CopyFrom(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        StartWithWindows = source.StartWithWindows;
        EnableMaterial = source.EnableMaterial;
        NewNoteHotkeyEnabled = source.NewNoteHotkeyEnabled;
        NewNoteHotkey = source.NewNoteHotkey;
        ManagerHotkeyEnabled = source.ManagerHotkeyEnabled;
        ManagerHotkey = source.ManagerHotkey;
    }

    public void Normalize()
    {
        NewNoteHotkey = string.IsNullOrWhiteSpace(NewNoteHotkey) ? "Ctrl+Shift+N" : NewNoteHotkey.Trim();
        ManagerHotkey = string.IsNullOrWhiteSpace(ManagerHotkey) ? "Ctrl+Shift+B" : ManagerHotkey.Trim();
    }
}
