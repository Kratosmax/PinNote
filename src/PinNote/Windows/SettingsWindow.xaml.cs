using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PinNote.Core.Models;
using PinNote.Services;

namespace PinNote.Windows;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<AppSettings, string?> _apply;
    private readonly Func<Task<string>> _checkForUpdates;

    public SettingsWindow(
        AppSettings settings,
        Func<AppSettings, string?> apply,
        Func<Task<string>> checkForUpdates,
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
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查…";
        try
        {
            UpdateStatusText.Text = await _checkForUpdates();
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

        var error = _apply(candidate);
        if (error is not null)
        {
            ErrorText.Text = error;
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
