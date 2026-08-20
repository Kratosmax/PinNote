using System.Diagnostics;
using System.Windows;
using PinNote.Core.Updates;
using PinNote.Infrastructure;

namespace PinNote.Windows;

public sealed partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private readonly Func<IProgress<int>, Task> _install;
    private readonly Action _skip;
    private readonly bool _canInstall;

    public UpdateWindow(
        UpdateInfo update,
        bool materialEnabled,
        bool canInstall,
        Func<IProgress<int>, Task> install,
        Action skip)
    {
        InitializeComponent();
        _update = update;
        _install = install;
        _skip = skip;
        _canInstall = canInstall;
        Tag = materialEnabled;
        VersionText.Text = $"PinNote {update.Version.ToString(3)} · {FormatSize(update.Size)}";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
            ? "此版本没有附加更新说明。"
            : update.ReleaseNotes;
        if (!canInstall)
        {
            InstallButton.Content = "打开下载页";
            StatusText.Text = "当前是开发构建或非标准便携目录，不能就地替换。";
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        NativeMethods.ApplyBackdrop(this, RootSurface, Tag is true);

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!_canInstall)
        {
            Process.Start(new ProcessStartInfo(_update.DownloadUri.ToString()) { UseShellExecute = true });
            return;
        }

        SetBusy(true);
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "正在下载更新…";
        try
        {
            var progress = new Progress<int>(value =>
            {
                DownloadProgress.Value = value;
                StatusText.Text = value < 100 ? $"正在下载和验证… {value}%" : "验证完成，正在重启…";
            });
            await _install(progress);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"更新失败：{exception.Message}";
            SetBusy(false);
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _skip();
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        InstallButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
        SkipButton.IsEnabled = !busy;
    }

    private static string FormatSize(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";
}
