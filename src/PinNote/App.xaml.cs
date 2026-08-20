using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using PinNote.Core.Models;
using PinNote.Core.Reminders;
using PinNote.Core.Storage;
using PinNote.Core.Updates;
using PinNote.Services;
using PinNote.Windows;
using Forms = System.Windows.Forms;

namespace PinNote;

public sealed partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\PinNote.SingleInstance.v1";
    private readonly Dictionary<Guid, NoteWindow> _noteWindows = [];
    private readonly Dictionary<Guid, ReminderWindow> _reminderWindows = [];
    private Mutex? _singleInstanceMutex;
    private Forms.NotifyIcon? _trayIcon;
    private ManagerWindow? _managerWindow;
    private NoteSnapshot _snapshot = new();
    private SaveCoordinator? _saveCoordinator;
    private ReminderScheduler? _reminderScheduler;
    private GlobalHotkeyService? _globalHotkeyService;
    private UpdateClient? _updateClient;
    private System.Threading.Timer? _updateTimer;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private UpdateWindow? _updateWindow;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, ResolveMutexName(), out var createdNew);
        if (!createdNew)
        {
            Infrastructure.NativeMethods.BroadcastShowExisting();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            WriteDiagnostic(args.Exception);
            Forms.MessageBox.Show(args.Exception.Message, "PinNote", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            args.Handled = true;
            if (_noteWindows.Count == 0)
            {
                _trayIcon?.Dispose();
                Shutdown(-1);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                WriteDiagnostic(exception);
            }
        };

        var store = new JsonNoteStore(ResolveDataPath());
        try
        {
            _snapshot = await store.LoadAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Forms.MessageBox.Show($"便签数据无法读取，将以空数据启动。\n\n{exception.Message}", "PinNote", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            _snapshot = new NoteSnapshot();
        }

        _saveCoordinator = new SaveCoordinator(store, () => _snapshot.Clone());
        _saveCoordinator.SaveFailed += OnSaveFailed;
        _reminderScheduler = new ReminderScheduler(() => Dispatcher.BeginInvoke(ProcessDueReminders));
        _updateClient = new UpdateClient();

        CreateTrayIcon();
        _globalHotkeyService = new GlobalHotkeyService(OnNewNoteHotkey, OnManagerHotkey);
        var hotkeyError = _globalHotkeyService.TryApply(_snapshot.Settings);
        WriteHotkeyProbe(hotkeyError is null ? "registration:ok" : $"registration:error:{hotkeyError}");
        if (hotkeyError is not null)
        {
            _trayIcon?.ShowBalloonTip(5000, "PinNote 快捷键未启用", hotkeyError, Forms.ToolTipIcon.Warning);
        }

        if (_snapshot.Notes.Count == 0)
        {
            _snapshot.Notes.Add(CreateDefaultNote());
            _saveCoordinator.MarkDirty();
        }

        var startInBackground = e.Args.Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        foreach (var note in _snapshot.Notes.ToArray())
        {
            var window = CreateNoteWindow(note);
            if (!startInBackground && !note.IsHidden)
            {
                window.Show();
            }
        }

        if (Environment.GetEnvironmentVariable("PINNOTE_SHOW_MANAGER") == "1")
        {
            ShowManager();
        }

        _reminderScheduler.Refresh(_snapshot.Notes);
        _ = Dispatcher.BeginInvoke(ResumeTriggeredReminders);
        ConfigureUpdateTimer();
        if (e.Args.Contains("--updated-from", StringComparer.OrdinalIgnoreCase))
        {
            _trayIcon?.ShowBalloonTip(4000, "PinNote 已更新", $"当前版本 {UpdateClient.CurrentVersion.ToString(3)}", Forms.ToolTipIcon.Info);
        }
        ScheduleUiTestCapture();
    }

    private static string ResolveDataPath()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("PINNOTE_DATA_DIR");
        var directory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinNote")
            : Path.GetFullPath(overrideDirectory);
        return Path.Combine(directory, "notes.json");
    }

    private static string ResolveMutexName()
    {
        var instanceId = Environment.GetEnvironmentVariable("PINNOTE_INSTANCE_ID");
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return MutexName;
        }
        var suffix = new string(instanceId.Where(char.IsLetterOrDigit).Take(32).ToArray());
        return suffix.Length == 0 ? MutexName : $"{MutexName}.{suffix}";
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("管理便签", null, (_, _) => Dispatcher.Invoke(ShowManager));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("新建便签", null, (_, _) => Dispatcher.Invoke(CreateNewNote));
        menu.Items.Add("显示全部", null, (_, _) => Dispatcher.Invoke(ShowAllNotes));
        menu.Items.Add("隐藏全部", null, (_, _) => Dispatcher.Invoke(HideAllNotes));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await Dispatcher.InvokeAsync(ExitApplication));

        var executableIcon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "PinNote",
            Icon = executableIcon ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowManager);
    }

    private NoteWindow CreateNoteWindow(NoteDocument note)
    {
        var window = new NoteWindow(note, () => _snapshot.Settings.EnableMaterial);
        window.Changed += changedWindow =>
        {
            changedWindow.Note.ModifiedAt = DateTimeOffset.Now;
            MarkDirty();
            RefreshManagerIfVisible();
        };
        window.ReminderChanged += _ =>
        {
            MarkDirty();
            _reminderScheduler?.Refresh(_snapshot.Notes);
        };
        window.NewRequested += _ => CreateNewNote();
        window.DeleteRequested += DeleteNote;
        window.HideRequested += changedWindow => SetNoteVisibility(changedWindow.Note, visible: false);
        _noteWindows[note.Id] = window;
        _ = new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    private NoteDocument CreateDefaultNote()
    {
        var index = _snapshot.Notes.Count;
        var area = SystemParameters.WorkArea;
        return new NoteDocument
        {
            Left = Math.Min(area.Right - 380, area.Left + 90 + index * 28),
            Top = Math.Min(area.Bottom - 440, area.Top + 90 + index * 28)
        };
    }

    private NoteDocument CreateNewNote()
    {
        var note = CreateDefaultNote();
        _snapshot.Notes.Add(note);
        var window = CreateNoteWindow(note);
        window.Show();
        window.Activate();
        MarkDirty();
        RefreshManagerIfVisible();
        return note;
    }

    private void OnNewNoteHotkey()
    {
        WriteHotkeyProbe("action:new-note");
        _ = CreateNewNote();
        WriteHotkeyProbe($"note-count:{_snapshot.Notes.Count}");
    }

    private void OnManagerHotkey()
    {
        WriteHotkeyProbe("action:manager");
        ShowManager();
        WriteHotkeyProbe($"manager-visible:{_managerWindow?.IsVisible == true}");
    }

    private static void WriteHotkeyProbe(string value)
    {
        var path = Environment.GetEnvironmentVariable("PINNOTE_HOTKEY_PROBE_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Test evidence must never affect application behavior.
        }
    }

    private void DeleteNote(NoteWindow window)
        => DeleteNote(window.Note);

    private void DeleteNote(NoteDocument note)
    {
        _snapshot.Notes.Remove(note);
        if (!_noteWindows.Remove(note.Id, out var window))
        {
            return;
        }
        if (_reminderWindows.Remove(note.Id, out var reminderWindow))
        {
            reminderWindow.Close();
        }
        window.AllowCloseAndClose();
        MarkDirty();
        _reminderScheduler?.Refresh(_snapshot.Notes);
        RefreshManagerIfVisible();
    }

    public void ShowAllNotes()
    {
        foreach (var (id, window) in _noteWindows)
        {
            var note = _snapshot.Notes.First(item => item.Id == id);
            note.IsHidden = false;
            window.ShowFromTray(activate: false);
        }

        _noteWindows.Values.LastOrDefault()?.Activate();
        MarkDirty();
        RefreshManagerIfVisible();
    }

    private void HideAllNotes()
    {
        foreach (var (id, window) in _noteWindows)
        {
            _snapshot.Notes.First(item => item.Id == id).IsHidden = true;
            window.Hide();
        }
        MarkDirty();
        RefreshManagerIfVisible();
    }

    public void ShowManager()
    {
        var manager = EnsureManagerWindow();
        manager.RefreshAll();
        Infrastructure.NativeMethods.TryActivate(manager);
        manager.Topmost = false;
    }

    private ManagerWindow EnsureManagerWindow()
    {
        if (_managerWindow is not null)
        {
            return _managerWindow;
        }

        _managerWindow = new ManagerWindow(
            _snapshot,
            CreateNewNote,
            note => SetNoteVisibility(note, visible: true, activate: true),
            (note, visible) => SetNoteVisibility(note, visible),
            DeleteNote,
            MarkDirty);
        _ = new WindowInteropHelper(_managerWindow).EnsureHandle();
        return _managerWindow;
    }

    private void SetNoteVisibility(NoteDocument note, bool visible, bool activate = false)
    {
        if (!_noteWindows.TryGetValue(note.Id, out var window))
        {
            return;
        }

        note.IsHidden = !visible;
        note.ModifiedAt = DateTimeOffset.Now;
        if (visible)
        {
            window.ShowFromTray(activate);
        }
        else
        {
            window.Hide();
        }
        MarkDirty();
        RefreshManagerIfVisible();
    }

    private void RefreshManagerIfVisible()
    {
        if (_managerWindow?.IsVisible == true)
        {
            _managerWindow.RefreshAll();
        }
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(
            _snapshot.Settings,
            TryApplySettings,
            () => CheckForUpdatesAsync(manual: true),
            UpdateClient.CurrentVersion)
        {
            Owner = _noteWindows.Values.FirstOrDefault(note => note.IsVisible)
        };
        window.ShowDialog();
    }

    private string? TryApplySettings(AppSettings candidate)
    {
        var previous = _snapshot.Settings.Clone();
        var hotkeyError = _globalHotkeyService?.TryApply(candidate);
        if (hotkeyError is not null)
        {
            return hotkeyError;
        }

        try
        {
            StartupRegistrationService.SetEnabled(candidate.StartWithWindows);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            _ = _globalHotkeyService?.TryApply(previous);
            return $"开机启动设置失败：{exception.Message}";
        }

        _snapshot.Settings.CopyFrom(candidate);
        foreach (var window in _noteWindows.Values)
        {
            window.RefreshMaterial();
        }
        MarkDirty();
        ConfigureUpdateTimer();
        return null;
    }

    private void ConfigureUpdateTimer()
    {
        _updateTimer?.Dispose();
        _updateTimer = null;
        if (!_snapshot.Settings.AutoUpdateEnabled || _isExiting)
        {
            return;
        }

        _updateTimer = new System.Threading.Timer(
            _ => _ = CheckForUpdatesAsync(manual: false),
            null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromHours(24));
    }

    private async Task<string> CheckForUpdatesAsync(bool manual)
    {
        if (_updateClient is null)
        {
            return "更新服务尚未初始化。";
        }
        if (!await _updateGate.WaitAsync(0))
        {
            return "正在检查更新，请稍候。";
        }

        try
        {
            var update = await _updateClient.CheckAsync();
            if (update is null)
            {
                return $"已是最新版本 {UpdateClient.CurrentVersion.ToString(3)}。";
            }
            if (!manual && update.Version.ToString(3) == _snapshot.Settings.SkippedUpdateVersion)
            {
                return "此版本已跳过。";
            }

            await Dispatcher.InvokeAsync(() => ShowUpdateWindow(update));
            return $"发现新版本 {update.Version.ToString(3)}。";
        }
        catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return "当前尚未发布可用的更新包。";
        }
        catch (Exception exception) when (!manual)
        {
            WriteDiagnostic(exception);
            return "后台更新检查失败。";
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private void ShowUpdateWindow(UpdateInfo update)
    {
        if (_updateWindow is { IsVisible: true })
        {
            Infrastructure.NativeMethods.TryActivate(_updateWindow);
            return;
        }

        _updateWindow = new UpdateWindow(
            update,
            _snapshot.Settings.EnableMaterial,
            _updateClient?.CanInstallInPlace == true,
            progress => InstallUpdateAsync(update, progress),
            () =>
            {
                _snapshot.Settings.SkippedUpdateVersion = update.Version.ToString(3);
                MarkDirty();
            })
        {
            Owner = _managerWindow?.IsVisible == true
                ? _managerWindow
                : _noteWindows.Values.FirstOrDefault(window => window.IsVisible)
        };
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        _updateWindow.Show();
    }

    private async Task InstallUpdateAsync(UpdateInfo update, IProgress<int> progress)
    {
        if (_updateClient is null)
        {
            throw new InvalidOperationException("更新服务尚未初始化。");
        }
        if (_saveCoordinator is not null)
        {
            await _saveCoordinator.FlushAsync();
        }
        var prepared = await _updateClient.DownloadAsync(update, progress);
        UpdateClient.LaunchUpdater(prepared);
        await ExitApplicationAsync();
    }

    private void ProcessDueReminders()
    {
        var now = DateTimeOffset.Now;
        var dueNotes = ReminderPlanner.GetDue(_snapshot.Notes, now);
        foreach (var note in dueNotes)
        {
            ReminderStateMachine.Trigger(note, now);
            PresentReminder(note);
        }

        if (dueNotes.Count > 0)
        {
            MarkDirty();
        }
        _reminderScheduler?.Refresh(_snapshot.Notes);
    }

    internal void RefreshRemindersForSystemChange() => ProcessDueReminders();

    private void ResumeTriggeredReminders()
    {
        foreach (var note in _snapshot.Notes.Where(note => note.ReminderState == ReminderState.Triggered))
        {
            PresentReminder(note);
        }
    }

    private void PresentReminder(NoteDocument note)
    {
        if (!_noteWindows.TryGetValue(note.Id, out var noteWindow))
        {
            return;
        }

        noteWindow.ShowFromTray(activate: false);
        noteWindow.ApplyReminderSignal(note.ReminderLevel);
        noteWindow.RefreshReminderStatus();

        switch (note.ReminderLevel)
        {
            case ReminderLevel.Weak:
                break;
            case ReminderLevel.Normal:
                _trayIcon?.ShowBalloonTip(5000, note.Title, "便签提醒已到时间。", Forms.ToolTipIcon.Info);
                break;
            case ReminderLevel.Strong:
            case ReminderLevel.Ultra:
                ShowReminderWindow(note, noteWindow);
                break;
        }
    }

    private void ShowReminderWindow(NoteDocument note, NoteWindow noteWindow)
    {
        if (_reminderWindows.TryGetValue(note.Id, out var existing))
        {
            if (note.ReminderLevel == ReminderLevel.Ultra)
            {
                Infrastructure.NativeMethods.TryActivate(existing);
            }
            return;
        }

        var reminder = new ReminderWindow(note, noteWindow.GetPlainText(), _snapshot.Settings.EnableMaterial);
        reminder.SnoozeRequested += _ =>
        {
            ReminderStateMachine.Snooze(note, DateTimeOffset.Now.AddMinutes(5));
            noteWindow.StopReminderSignal();
            noteWindow.RefreshReminderStatus();
            MarkDirty();
            _reminderScheduler?.Refresh(_snapshot.Notes);
        };
        reminder.DismissRequested += _ =>
        {
            ReminderStateMachine.Dismiss(note);
            noteWindow.StopReminderSignal();
            noteWindow.RefreshReminderStatus();
            MarkDirty();
        };
        reminder.CompleteRequested += _ =>
        {
            ReminderStateMachine.Complete(note);
            noteWindow.StopReminderSignal();
            noteWindow.RefreshReminderStatus();
            MarkDirty();
            _reminderScheduler?.Refresh(_snapshot.Notes);
        };
        reminder.Closed += (_, _) => _reminderWindows.Remove(note.Id);
        _reminderWindows[note.Id] = reminder;
        reminder.Show();
    }

    private void MarkDirty()
    {
        foreach (var window in _noteWindows.Values)
        {
            window.SetSaveError(false);
        }
        _saveCoordinator?.MarkDirty();
    }

    private void OnSaveFailed(Exception exception)
    {
        foreach (var window in _noteWindows.Values)
        {
            window.SetSaveError(true);
        }
        _trayIcon?.ShowBalloonTip(5000, "PinNote 保存失败", exception.Message, Forms.ToolTipIcon.Error);
    }

    private void ScheduleUiTestCapture()
    {
        var capturePath = Environment.GetEnvironmentVariable("PINNOTE_CAPTURE_PATH");
        if (string.IsNullOrWhiteSpace(capturePath))
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(900);
                var window = _noteWindows.Values.First();
                VisualCaptureService.Capture(window, capturePath);
                var statePath = Path.ChangeExtension(capturePath, ".json");
                var state = new
                {
                    window.IsVisible,
                    window.IsActive,
                    window.ActualWidth,
                    window.ActualHeight,
                    window.Left,
                    window.Top,
                    window.Topmost,
                    window.Note.PinMode,
                    ProcessId = Environment.ProcessId
                };
                await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

                if (Environment.GetEnvironmentVariable("PINNOTE_CAPTURE_SUITE") == "1")
                {
                    await CaptureVisualSuite(window, capturePath);
                }

                if (Environment.GetEnvironmentVariable("PINNOTE_EXIT_AFTER_CAPTURE") == "1")
                {
                    ExitApplication();
                }
            }
            catch (Exception exception)
            {
                WriteDiagnostic(exception);
            }
        });
    }

    private async Task CaptureVisualSuite(NoteWindow window, string initialCapturePath)
    {
        var directory = Path.GetDirectoryName(initialCapturePath)
            ?? throw new InvalidOperationException("The capture path has no parent directory.");

        window.Width = 300;
        window.Height = 360;
        window.ConfigureVisualTest(
            "下周发布前需要确认的长标题",
            "核对版本号、更新清单和下载哈希。\n\n这是一段用于检查换行、长文本和窄窗口布局的测试内容。",
            showReminderEditor: true);
        await Task.Delay(120);
        VisualCaptureService.Capture(window, Path.Combine(directory, "note-long-reminder.png"));

        ReminderStateMachine.Schedule(window.Note, DateTimeOffset.Now.AddMinutes(-8), ReminderLevel.Normal);
        window.ConfigureVisualTest(window.Note.Title, window.GetPlainText(), showReminderEditor: false);
        window.RefreshReminderStatus();
        window.ApplyReminderSignal(ReminderLevel.Normal);
        await Task.Delay(220);
        VisualCaptureService.Capture(window, Path.Combine(directory, "note-overdue.png"));
        window.StopReminderSignal();
        await Task.Delay(80);
        VisualCaptureService.Capture(window, Path.Combine(directory, "note-overdue-static.png"));

        var reminderNote = window.Note.Clone();
        reminderNote.ReminderLevel = ReminderLevel.Strong;
        var reminder = new ReminderWindow(reminderNote, window.GetPlainText(), _snapshot.Settings.EnableMaterial);
        reminder.Show();
        await Task.Delay(220);
        VisualCaptureService.Capture(reminder, Path.Combine(directory, "strong-reminder.png"));
        reminder.CloseWithoutAction();

        var settings = new SettingsWindow(
            _snapshot.Settings,
            _ => null,
            () => Task.FromResult("视觉测试"),
            UpdateClient.CurrentVersion);
        settings.Show();
        await Task.Delay(120);
        VisualCaptureService.Capture(settings, Path.Combine(directory, "settings.png"));
        settings.Close();

        var visualUpdate = new UpdateInfo(
            new Version(0, 4, 1),
            UpdateTrust.Channel,
            new Uri("https://github.com/Kratosmax/PinNote/releases/download/v0.4.1/PinNote-0.4.1-Lite-Portable.zip"),
            8_400_000,
            new string('A', 64),
            "新增安全自动更新，并优化提醒窗口在高 DPI 下的稳定性。\n\n下载后会验证签名、哈希与包内版本。",
            string.Empty);
        var updateWindow = new UpdateWindow(
            visualUpdate,
            _snapshot.Settings.EnableMaterial,
            canInstall: false,
            _ => Task.CompletedTask,
            () => { });
        updateWindow.Show();
        await Task.Delay(120);
        VisualCaptureService.Capture(updateWindow, Path.Combine(directory, "update-available.png"));
        updateWindow.Close();

        var visualGroup = new NoteGroup { Name = "发布计划", SortOrder = 0 };
        _snapshot.Groups.Add(visualGroup);
        _snapshot.Notes.Add(new NoteDocument
        {
            Title = "核对安装包与自动更新清单",
            GroupId = visualGroup.Id,
            ModifiedAt = DateTimeOffset.Now.AddMinutes(-3),
            ReminderAt = DateTimeOffset.Now.AddHours(2)
        });
        _snapshot.Notes.Add(new NoteDocument
        {
            Title = "稍后整理的灵感",
            IsHidden = true,
            ModifiedAt = DateTimeOffset.Now.AddHours(-2)
        });
        var manager = EnsureManagerWindow();
        manager.RefreshAll();
        manager.Show();
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager.png"));
        manager.Hide();
    }

    private static void WriteDiagnostic(Exception exception)
    {
        try
        {
            var directory = Environment.GetEnvironmentVariable("PINNOTE_DATA_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "diagnostic.log"), $"[{DateTimeOffset.Now:O}]\n{exception}\n\n");
        }
        catch
        {
            // Diagnostics must never become a second application failure.
        }
    }

    private async void ExitApplication() => await ExitApplicationAsync();

    private async Task ExitApplicationAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        foreach (var window in _noteWindows.Values)
        {
            window.CaptureGeometry();
        }
        _saveCoordinator?.MarkDirty();
        if (_saveCoordinator is not null)
        {
            await _saveCoordinator.FlushAsync();
        }

        foreach (var reminder in _reminderWindows.Values.ToArray())
        {
            reminder.CloseWithoutAction();
        }
        foreach (var window in _noteWindows.Values.ToArray())
        {
            window.AllowCloseAndClose();
        }

        _managerWindow?.AllowCloseAndClose();

        _trayIcon?.Dispose();
        _reminderScheduler?.Dispose();
        _updateTimer?.Dispose();
        _updateClient?.Dispose();
        _globalHotkeyService?.Dispose();
        _saveCoordinator?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown();
    }
}
