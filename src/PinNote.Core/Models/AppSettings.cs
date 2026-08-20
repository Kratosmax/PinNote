namespace PinNote.Core.Models;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }

    public bool EnableMaterial { get; set; } = true;

    public bool AutoUpdateEnabled { get; set; } = true;

    public string SkippedUpdateVersion { get; set; } = string.Empty;

    public bool NewNoteHotkeyEnabled { get; set; } = true;

    public string NewNoteHotkey { get; set; } = "Ctrl+Shift+N";

    public bool ManagerHotkeyEnabled { get; set; } = true;

    public string ManagerHotkey { get; set; } = "Ctrl+Shift+B";

    public UpdateNetworkSettings UpdateNetwork { get; set; } = UpdateNetworkSettings.Default;

    public AppSettings Clone() => new()
    {
        StartWithWindows = StartWithWindows,
        EnableMaterial = EnableMaterial,
        AutoUpdateEnabled = AutoUpdateEnabled,
        SkippedUpdateVersion = SkippedUpdateVersion,
        NewNoteHotkeyEnabled = NewNoteHotkeyEnabled,
        NewNoteHotkey = NewNoteHotkey,
        ManagerHotkeyEnabled = ManagerHotkeyEnabled,
        ManagerHotkey = ManagerHotkey,
        UpdateNetwork = new UpdateNetworkSettings(UpdateNetwork.GithubProxies?.ToList(), UpdateNetwork.HttpProxy).Normalize()
    };

    public void CopyFrom(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        StartWithWindows = source.StartWithWindows;
        EnableMaterial = source.EnableMaterial;
        AutoUpdateEnabled = source.AutoUpdateEnabled;
        SkippedUpdateVersion = source.SkippedUpdateVersion;
        NewNoteHotkeyEnabled = source.NewNoteHotkeyEnabled;
        NewNoteHotkey = source.NewNoteHotkey;
        ManagerHotkeyEnabled = source.ManagerHotkeyEnabled;
        ManagerHotkey = source.ManagerHotkey;
        UpdateNetwork = new UpdateNetworkSettings(source.UpdateNetwork.GithubProxies?.ToList(), source.UpdateNetwork.HttpProxy).Normalize();
    }

    public void Normalize()
    {
        NewNoteHotkey = string.IsNullOrWhiteSpace(NewNoteHotkey) ? "Ctrl+Shift+N" : NewNoteHotkey.Trim();
        ManagerHotkey = string.IsNullOrWhiteSpace(ManagerHotkey) ? "Ctrl+Shift+B" : ManagerHotkey.Trim();
        SkippedUpdateVersion = SkippedUpdateVersion?.Trim() ?? string.Empty;
        UpdateNetwork = (UpdateNetwork ?? UpdateNetworkSettings.Default).Normalize();
    }
}
