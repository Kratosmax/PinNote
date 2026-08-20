using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PinNote.Core.Models;
using PinNote.Infrastructure;
using PinNote.Services;

namespace PinNote.Windows;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<AppSettings, string?> _apply;
    private readonly Func<UpdateNetworkSettings, Task<string>> _checkForUpdates;
    private readonly ObservableCollection<GithubProxyEditorRow> _githubProxies = [];

    public SettingsWindow(
        AppSettings settings,
        Func<AppSettings, string?> apply,
        Func<UpdateNetworkSettings, Task<string>> checkForUpdates,
        Version currentVersion)
    {
        InitializeComponent();
        _settings = settings;
        _apply = apply;
        _checkForUpdates = checkForUpdates;
        StartWithWindowsBox.IsChecked = settings.StartWithWindows;
        MaterialBox.IsChecked = settings.EnableMaterial;
        AutoUpdateBox.IsChecked = settings.AutoUpdateEnabled;
        CurrentVersionText.Text = $"当前版本 {currentVersion.ToString(3)}";
        NewNoteHotkeyEnabledBox.IsChecked = settings.NewNoteHotkeyEnabled;
        NewNoteHotkeyBox.Text = settings.NewNoteHotkey;
        ManagerHotkeyEnabledBox.IsChecked = settings.ManagerHotkeyEnabled;
        ManagerHotkeyBox.Text = settings.ManagerHotkey;
        foreach (var proxy in settings.UpdateNetwork.Normalize().GithubProxies ?? [])
        {
            _githubProxies.Add(new GithubProxyEditorRow(proxy));
        }
        GithubProxyGrid.ItemsSource = _githubProxies;
        HttpProxyBox.Text = settings.UpdateNetwork.Normalize().HttpProxy ?? string.Empty;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        NativeMethods.ApplyBackdrop(this, RootSurface, MaterialBox.IsChecked == true);

    internal void ShowNetworkSettingsForVisualQa() => SettingsTabs.SelectedIndex = 1;

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildNetworkSettings(out var networkSettings))
        {
            return;
        }
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";
        try
        {
            UpdateStatusText.Text = await _checkForUpdates(networkSettings);
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"检查失败：{exception.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void AddProxy_Click(object sender, RoutedEventArgs e)
    {
        var row = new GithubProxyEditorRow(new GithubProxySetting("https://", 5));
        _githubProxies.Add(row);
        GithubProxyGrid.SelectedItem = row;
        GithubProxyGrid.ScrollIntoView(row);
    }

    private void RemoveProxy_Click(object sender, RoutedEventArgs e)
    {
        if (GithubProxyGrid.SelectedItem is not GithubProxyEditorRow row)
        {
            return;
        }
        if (row.IsDirect)
        {
            ErrorText.Text = "GitHub 直连不可删除；可将优先级设为 0 来禁用。";
            return;
        }
        _githubProxies.Remove(row);
        ErrorText.Text = string.Empty;
    }

    private void GithubProxyGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is GithubProxyEditorRow { IsDirect: true } && e.Column.DisplayIndex == 0)
        {
            e.Cancel = true;
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        ErrorText.Text = string.Empty;
        if (GlobalHotkeyService.TryFromKeyEvent(e, out var normalized))
        {
            ((TextBox)sender).Text = normalized;
        }
    }

    private void ResetNewNoteHotkey_Click(object sender, RoutedEventArgs e)
    {
        NewNoteHotkeyBox.Text = "Ctrl+Shift+N";
        NewNoteHotkeyEnabledBox.IsChecked = true;
        ErrorText.Text = string.Empty;
    }

    private void ResetManagerHotkey_Click(object sender, RoutedEventArgs e)
    {
        ManagerHotkeyBox.Text = "Ctrl+Shift+B";
        ManagerHotkeyEnabledBox.IsChecked = true;
        ErrorText.Text = string.Empty;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var candidate = _settings.Clone();
        candidate.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        candidate.EnableMaterial = MaterialBox.IsChecked == true;
        candidate.AutoUpdateEnabled = AutoUpdateBox.IsChecked == true;
        candidate.NewNoteHotkeyEnabled = NewNoteHotkeyEnabledBox.IsChecked == true;
        candidate.NewNoteHotkey = NewNoteHotkeyBox.Text;
        candidate.ManagerHotkeyEnabled = ManagerHotkeyEnabledBox.IsChecked == true;
        candidate.ManagerHotkey = ManagerHotkeyBox.Text;
        if (!TryBuildNetworkSettings(out var networkSettings))
        {
            return;
        }
        candidate.UpdateNetwork = networkSettings;

        var error = _apply(candidate);
        if (error is not null)
        {
            ErrorText.Text = error;
            return;
        }
        DialogResult = true;
    }

    private bool TryBuildNetworkSettings(out UpdateNetworkSettings settings)
    {
        GithubProxyGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        GithubProxyGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var proxies = new List<GithubProxySetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _githubProxies)
        {
            if (row.IsDirect)
            {
                proxies.Add(new GithubProxySetting(string.Empty, row.Priority, true));
                continue;
            }
            if (!UpdateNetworkSettings.TryNormalizeGithubProxy(row.Address, out var baseUrl))
            {
                ErrorText.Text = $"GitHub 前缀线路无效：{row.Address}。请输入完整的 http:// 或 https:// 地址，且不要包含查询参数。";
                settings = UpdateNetworkSettings.Default;
                return false;
            }
            if (!seen.Add(baseUrl))
            {
                ErrorText.Text = $"GitHub 前缀线路重复：{baseUrl}";
                settings = UpdateNetworkSettings.Default;
                return false;
            }
            proxies.Add(new GithubProxySetting(baseUrl, row.Priority));
        }

        if (!UpdateNetworkSettings.TryNormalizeHttpProxy(HttpProxyBox.Text, out var httpProxy))
        {
            ErrorText.Text = "HTTP 网络代理无效。请输入类似 http://127.0.0.1:7890 的地址；暂不支持账号密码。";
            settings = UpdateNetworkSettings.Default;
            return false;
        }
        if (proxies.All(item => item.Priority == 0))
        {
            ErrorText.Text = "至少启用一条 GitHub 访问线路，优先级需设为 1 到 10。";
            settings = UpdateNetworkSettings.Default;
            return false;
        }

        ErrorText.Text = string.Empty;
        settings = new UpdateNetworkSettings(proxies, httpProxy).Normalize();
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

internal sealed class GithubProxyEditorRow
{
    public GithubProxyEditorRow(GithubProxySetting setting)
    {
        IsDirect = setting.IsDirect;
        Address = setting.IsDirect ? "GitHub 直连（不拼接前缀）" : setting.BaseUrl;
        Priority = setting.Priority;
    }

    public string Address { get; set; }
    public int Priority { get; set; }
    public bool IsDirect { get; }
}
