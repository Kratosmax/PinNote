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

    public List<string> FavoriteTextColors { get; set; } = [];

    public bool AutoCompleteParentTodo { get; set; }

    public int RecycleBinRetentionDays { get; set; } = 30;

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
        UpdateNetwork = new UpdateNetworkSettings(UpdateNetwork.GithubProxies?.ToList(), UpdateNetwork.HttpProxy).Normalize(),
        FavoriteTextColors = FavoriteTextColors?.ToList() ?? [],
        AutoCompleteParentTodo = AutoCompleteParentTodo,
        RecycleBinRetentionDays = RecycleBinRetentionDays
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
        FavoriteTextColors = source.FavoriteTextColors?.ToList() ?? [];
        AutoCompleteParentTodo = source.AutoCompleteParentTodo;
        RecycleBinRetentionDays = source.RecycleBinRetentionDays;
    }

    public void Normalize()
    {
        NewNoteHotkey = string.IsNullOrWhiteSpace(NewNoteHotkey) ? "Ctrl+Shift+N" : NewNoteHotkey.Trim();
        ManagerHotkey = string.IsNullOrWhiteSpace(ManagerHotkey) ? "Ctrl+Shift+B" : ManagerHotkey.Trim();
        SkippedUpdateVersion = SkippedUpdateVersion?.Trim() ?? string.Empty;
        UpdateNetwork = (UpdateNetwork ?? UpdateNetworkSettings.Default).Normalize();
        FavoriteTextColors = NormalizeFavoriteTextColors(FavoriteTextColors);
        RecycleBinRetentionDays = Math.Clamp(RecycleBinRetentionDays, 1, 3650);
    }

    public bool RememberFavoriteTextColor(string value)
    {
        var normalized = NormalizeTextColor(value);
        if (normalized is null || PermanentTextColors.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var next = new[] { normalized }
            .Concat(FavoriteTextColors ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        if (next.SequenceEqual(FavoriteTextColors ?? [], StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        FavoriteTextColors = next;
        return true;
    }

    private static readonly string[] PermanentTextColors = ["#202428", "#147D76", "#C95B52"];

    private static List<string> NormalizeFavoriteTextColors(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(NormalizeTextColor)
            .Where(value => value is not null && !PermanentTextColors.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

    private static string? NormalizeTextColor(string? value)
    {
        var text = value?.Trim();
        if (text is null || text.Length != 7 || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return text.ToUpperInvariant();
    }
}
