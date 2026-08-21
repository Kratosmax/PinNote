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
    private readonly Dictionary<Guid, TodoGroupWindow> _todoGroupWindows = [];
    private readonly Dictionary<Guid, ReminderWindow> _reminderWindows = [];
    private readonly Dictionary<Guid, ReminderWindow> _todoReminderWindows = [];
    private Mutex? _singleInstanceMutex;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _trayIconImage;
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

    internal bool MaterialEnabled => _snapshot.Settings.EnableMaterial;

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

        var purgedItems = ItemLifecycle.PurgeExpired(
            _snapshot, DateTimeOffset.Now, _snapshot.Settings.RecycleBinRetentionDays);
        _saveCoordinator = new SaveCoordinator(store, () => _snapshot.Clone());
        _saveCoordinator.SaveFailed += OnSaveFailed;
        if (purgedItems > 0) _saveCoordinator.MarkDirty();
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

        if (_snapshot.Notes.All(note => note.DeletedAt is not null))
        {
            _snapshot.Notes.Add(CreateDefaultNote());
            _saveCoordinator.MarkDirty();
        }

        var startInBackground = e.Args.Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        foreach (var note in _snapshot.Notes.Where(note => note.DeletedAt is null).ToArray())
        {
            var window = CreateNoteWindow(note);
            if (!startInBackground && !note.IsHidden)
            {
                window.Show();
            }
        }
        foreach (var group in _snapshot.TodoGroups.ToArray())
        {
            var window = CreateTodoGroupWindow(group);
            if (!startInBackground && !group.IsHidden)
            {
                window.Show();
            }
        }

        if (Environment.GetEnvironmentVariable("PINNOTE_SHOW_MANAGER") == "1")
        {
            ShowManager();
        }

        _reminderScheduler.Refresh(_snapshot.Notes, _snapshot.TodoItems);
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
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("管理便签与待办", null, (_, _) => Dispatcher.Invoke(ShowManager));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        var noteMenu = new Forms.ToolStripMenuItem("便签");
        noteMenu.DropDownItems.Add("新建便签", null, (_, _) => Dispatcher.Invoke(CreateNewNote));
        noteMenu.DropDownItems.Add("打开便签管理", null, (_, _) => Dispatcher.Invoke(() => { ShowManager(); _managerWindow?.ShowNoteMode(); }));
        noteMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        noteMenu.DropDownItems.Add("显示全部便签窗口", null, (_, _) => Dispatcher.Invoke(ShowAllNoteWindows));
        noteMenu.DropDownItems.Add("隐藏全部便签窗口", null, (_, _) => Dispatcher.Invoke(HideAllNoteWindows));
        _trayMenu.Items.Add(noteMenu);
        var todoMenu = new Forms.ToolStripMenuItem("待办");
        todoMenu.DropDownItems.Add("新建待办分组", null, (_, _) => Dispatcher.Invoke(CreateNewTodoGroupFromTray));
        todoMenu.DropDownItems.Add("打开待办管理", null, (_, _) => Dispatcher.Invoke(() => { ShowManager(); _managerWindow?.ShowTodoMode(); }));
        todoMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        todoMenu.DropDownItems.Add("显示全部待办窗口", null, (_, _) => Dispatcher.Invoke(ShowAllTodoGroups));
        todoMenu.DropDownItems.Add("隐藏全部待办窗口", null, (_, _) => Dispatcher.Invoke(HideAllTodoGroups));
        _trayMenu.Items.Add(todoMenu);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { ShowManager(); _managerWindow?.ShowSettingsMode(); }));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, async (_, _) => await Dispatcher.InvokeAsync(ExitApplication));

        _trayIconImage = LoadTrayIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "PinNote",
            Icon = _trayIconImage ?? System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowManager);
    }

    private static System.Drawing.Icon? LoadTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/pinnote.ico"));
        if (resource is not null)
        {
            using var stream = resource.Stream;
            using var icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            return (System.Drawing.Icon)icon.Clone();
        }

        return !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;
    }

    private NoteWindow CreateNoteWindow(NoteDocument note)
    {
        var window = new NoteWindow(
            note,
            () => _snapshot.Settings.EnableMaterial,
            () => _snapshot.Settings.FavoriteTextColors);
        window.Changed += changedWindow =>
        {
            changedWindow.Note.ModifiedAt = DateTimeOffset.Now;
            MarkDirty();
            RefreshManagerIfVisible();
        };
        window.ReminderChanged += _ =>
        {
            MarkDirty();
            _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
        };
        window.NewRequested += _ => CreateNewNote();
        window.DeleteRequested += DeleteNote;
        window.DuplicateRequested += changedWindow => DuplicateNote(changedWindow.Note);
        window.HideRequested += changedWindow => SetNoteVisibility(changedWindow.Note, visible: false);
        window.FavoriteTextColorAdded += RememberFavoriteTextColor;
        _noteWindows[note.Id] = window;
        _ = new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    private TodoGroupWindow CreateTodoGroupWindow(TodoGroup group)
    {
        var window = new TodoGroupWindow(group, _snapshot, () => _snapshot.Settings.EnableMaterial);
        window.Changed += _ => OnTodoGroupWindowChanged();
        window.GeometryChanged += _ => MarkDirty();
        window.HideRequested += changedWindow => SetTodoGroupVisibility(changedWindow.Group, visible: false);
        window.OpenManagerRequested += _ => ShowManager();
        window.NewGroupRequested += CreateNewTodoGroup;
        _todoGroupWindows[group.Id] = window;
        _ = new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    private void CreateNewTodoGroupFromTray()
    {
        const string baseName = "新待办分组";
        var usedNames = _snapshot.TodoGroups.Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        for (var suffix = 2; usedNames.Contains(name); suffix++) name = $"{baseName} {suffix}";
        var area = SystemParameters.WorkArea;
        var offset = _snapshot.TodoGroups.Count * 24;
        var group = new TodoGroup
        {
            Name = name,
            SortOrder = _snapshot.TodoGroups.Count,
            Left = Math.Min(area.Right - 430, area.Left + 120 + offset),
            Top = Math.Min(area.Bottom - 500, area.Top + 100 + offset)
        };
        _snapshot.TodoGroups.Add(group);
        var window = CreateTodoGroupWindow(group);
        window.ShowFromTray(activate: true);
        MarkDirty();
        RefreshManagerIfVisible();
    }

    private void CreateNewTodoGroup(TodoGroupWindow sourceWindow)
    {
        const string baseName = "新待办分组";
        var usedNames = _snapshot.TodoGroups.Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = baseName;
        for (var suffix = 2; usedNames.Contains(name); suffix++) name = $"{baseName} {suffix}";
        var area = SystemParameters.WorkArea;
        var group = new TodoGroup
        {
            Name = name,
            SortOrder = _snapshot.TodoGroups.Count,
            Left = Math.Min(area.Right - Math.Max(sourceWindow.ActualWidth, 430), sourceWindow.Left + 28),
            Top = Math.Min(area.Bottom - Math.Max(sourceWindow.ActualHeight, 300), sourceWindow.Top + 28),
            Width = Math.Max(sourceWindow.ActualWidth, 430),
            Height = Math.Max(sourceWindow.ActualHeight, 300)
        };
        _snapshot.TodoGroups.Add(group);
        var window = CreateTodoGroupWindow(group);
        window.ShowFromTray(activate: true);
        MarkDirty();
        RefreshManagerIfVisible();
    }
    private void OnTodoGroupWindowChanged()
    {
        MarkDirty();
        _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
        RefreshManagerIfVisible();
    }

    private void RefreshTodoGroupWindows()
    {
        foreach (var window in _todoGroupWindows.Values)
        {
            window.RefreshData();
        }
    }

    private void SyncTodoGroupWindows()
    {
        var groupIds = _snapshot.TodoGroups.Select(group => group.Id).ToHashSet();
        foreach (var id in _todoGroupWindows.Keys.Where(id => !groupIds.Contains(id)).ToArray())
        {
            _todoGroupWindows[id].AllowCloseAndClose();
            _todoGroupWindows.Remove(id);
        }

        foreach (var todoId in _todoReminderWindows.Keys.Where(todoId =>
                     _snapshot.TodoItems.All(todo => todo.Id != todoId || todo.DeletedAt is not null)).ToArray())
        {
            _todoReminderWindows[todoId].CloseWithoutAction();
            _todoReminderWindows.Remove(todoId);
        }

        foreach (var group in _snapshot.TodoGroups)
        {
            if (!_todoGroupWindows.TryGetValue(group.Id, out var window))
            {
                window = CreateTodoGroupWindow(group);
                if (!group.IsHidden)
                {
                    window.ShowFromTray(activate: false);
                }
            }
            window.RefreshData();
        }
    }

    private void RememberFavoriteTextColor(string color)
    {
        if (!_snapshot.Settings.RememberFavoriteTextColor(color))
        {
            return;
        }

        foreach (var window in _noteWindows.Values)
        {
            window.RefreshFavoriteTextColors();
        }
        MarkDirty();
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
        ItemLifecycle.MoveToTrash(note, DateTimeOffset.Now);
        if (_noteWindows.Remove(note.Id, out var window)) window.AllowCloseAndClose();
        if (_reminderWindows.Remove(note.Id, out var reminderWindow)) reminderWindow.CloseWithoutAction();
        MarkDirty();
        _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
        RefreshManagerIfVisible();
    }

    private NoteDocument DuplicateNote(NoteDocument source)
    {
        var copy = ItemLifecycle.Duplicate(source, DateTimeOffset.Now);
        _snapshot.Notes.Add(copy);
        var window = CreateNoteWindow(copy);
        window.ShowFromTray(activate: true);
        MarkDirty();
        RefreshManagerIfVisible();
        return copy;
    }

    private void RestoreNote(NoteDocument note)
    {
        ItemLifecycle.Restore(note);
        if (!_noteWindows.ContainsKey(note.Id)) CreateNoteWindow(note);
        MarkDirty();
        _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
        RefreshManagerIfVisible();
    }

    private void PurgeNote(NoteDocument note)
    {
        if (_noteWindows.Remove(note.Id, out var window)) window.AllowCloseAndClose();
        _snapshot.Notes.Remove(note);
        MarkDirty();
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

        foreach (var (id, window) in _todoGroupWindows)
        {
            var group = _snapshot.TodoGroups.First(item => item.Id == id);
            group.IsHidden = false;
            window.ShowFromTray(activate: false);
        }

        (_todoGroupWindows.Values.LastOrDefault() as Window ?? _noteWindows.Values.LastOrDefault())?.Activate();
        MarkDirty();
        RefreshManagerIfVisible();
    }

    private void ShowAllNoteWindows()
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

    private void HideAllNoteWindows()
    {
        foreach (var (id, window) in _noteWindows)
        {
            _snapshot.Notes.First(item => item.Id == id).IsHidden = true;
            window.Hide();
        }
        MarkDirty();
        RefreshManagerIfVisible();
    }

    private void ShowAllTodoGroups()
    {
        foreach (var (id, window) in _todoGroupWindows)
        {
            var group = _snapshot.TodoGroups.First(item => item.Id == id);
            group.IsHidden = false;
            window.ShowFromTray(activate: false);
        }
        _todoGroupWindows.Values.LastOrDefault()?.Activate();
        MarkDirty();
        RefreshManagerIfVisible();
    }

    private void HideAllTodoGroups()
    {
        foreach (var (id, window) in _todoGroupWindows)
        {
            _snapshot.TodoGroups.First(item => item.Id == id).IsHidden = true;
            window.Hide();
        }
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
        foreach (var (id, window) in _todoGroupWindows)
        {
            _snapshot.TodoGroups.First(item => item.Id == id).IsHidden = true;
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
            DuplicateNote,
            RestoreNote,
            PurgeNote,
            (group, visible) => SetTodoGroupVisibility(group, visible, activate: true),
            OnManagerDataChanged,
            () => new SettingsPanel(_snapshot.Settings, TryApplySettings, network => CheckForUpdatesAsync(manual: true, network), UpdateClient.CurrentVersion));
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

    private void SetTodoGroupVisibility(TodoGroup group, bool visible, bool activate = false)
    {
        if (!_todoGroupWindows.TryGetValue(group.Id, out var window))
        {
            return;
        }

        group.IsHidden = !visible;
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

    private void OnManagerDataChanged()
    {
        SyncTodoGroupWindows();
        MarkDirty();
        _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
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
        foreach (var window in _todoGroupWindows.Values)
        {
            window.RefreshMaterial();
        }
        MarkDirty();
        ConfigureUpdateTimer();
        RefreshManagerIfVisible();
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

    private async Task<string> CheckForUpdatesAsync(bool manual, UpdateNetworkSettings? networkSettings = null)
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
            var effectiveNetwork = (networkSettings ?? _snapshot.Settings.UpdateNetwork).Normalize();
            var update = await _updateClient.CheckAsync(effectiveNetwork);
            if (update is null)
            {
                return $"已是最新版本 {UpdateClient.CurrentVersion.ToString(3)}。";
            }
            if (!manual && update.Version.ToString(3) == _snapshot.Settings.SkippedUpdateVersion)
            {
                return "此版本已跳过。";
            }

            await Dispatcher.InvokeAsync(() => ShowUpdateWindow(update, effectiveNetwork));
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

    private void ShowUpdateWindow(UpdateInfo update, UpdateNetworkSettings networkSettings)
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
            progress => InstallUpdateAsync(update, progress, networkSettings),
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

    private async Task InstallUpdateAsync(
        UpdateInfo update,
        IProgress<int> progress,
        UpdateNetworkSettings networkSettings)
    {
        if (_updateClient is null)
        {
            throw new InvalidOperationException("更新服务尚未初始化。");
        }
        if (_saveCoordinator is not null)
        {
            await _saveCoordinator.FlushAsync();
        }
        var prepared = await _updateClient.DownloadAsync(update, progress, networkSettings);
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

        var dueTodos = TodoPlanner.GetDue(_snapshot.TodoItems, now);
        foreach (var todo in dueTodos)
        {
            TodoPlanner.Trigger(todo, now);
            PresentTodoReminder(todo);
        }

        if (dueNotes.Count > 0 || dueTodos.Count > 0)
        {
            MarkDirty();
            RefreshManagerIfVisible();
        }
        _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
    }

    internal void RefreshRemindersForSystemChange() => ProcessDueReminders();

    private void ResumeTriggeredReminders()
    {
        foreach (var note in _snapshot.Notes.Where(note => note.DeletedAt is null && note.ReminderState == ReminderState.Triggered))
        {
            PresentReminder(note);
        }
        foreach (var todo in _snapshot.TodoItems.Where(todo => todo.DeletedAt is null && todo.ReminderState == ReminderState.Triggered))
        {
            PresentTodoReminder(todo);
        }
    }

    private void PresentReminder(NoteDocument note)
    {
        if (!_noteWindows.TryGetValue(note.Id, out var noteWindow))
        {
            return;
        }

        noteWindow.ShowFromTray(activate: false);
        noteWindow.RefreshReminderStatus();
        noteWindow.ApplyReminderSignal(note.ReminderLevel);

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
        PlaceReminderWindow(reminder);
        reminder.SnoozeRequested += (_, due) =>
        {
            ReminderStateMachine.Snooze(note, due);
            noteWindow.StopReminderSignal();
            noteWindow.RefreshReminderStatus();
            MarkDirty();
            _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
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
            _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
        };
        reminder.Closed += (_, _) => _reminderWindows.Remove(note.Id);
        _reminderWindows[note.Id] = reminder;
        reminder.Show();
    }

    private void PresentTodoReminder(TodoItem todo)
    {
        if (!_todoGroupWindows.TryGetValue(todo.GroupId, out var groupWindow))
        {
            return;
        }

        var group = _snapshot.TodoGroups.FirstOrDefault(item => item.Id == todo.GroupId);
        if (group is not null)
        {
            group.IsHidden = false;
        }
        groupWindow.ShowFromTray(activate: false);
        groupWindow.RefreshData();
        groupWindow.ApplyReminderSignal(todo.ReminderLevel);

        switch (todo.ReminderLevel)
        {
            case ReminderLevel.Weak:
                break;
            case ReminderLevel.Normal:
                _trayIcon?.ShowBalloonTip(5000, todo.Title, "待办提醒已到时间。", Forms.ToolTipIcon.Info);
                break;
            case ReminderLevel.Strong:
            case ReminderLevel.Ultra:
                ShowTodoReminderWindow(todo, groupWindow);
                break;
        }
    }

    private void PlaceReminderWindow(ReminderWindow reminder)
    {
        var area = SystemParameters.WorkArea;
        var index = _reminderWindows.Count + _todoReminderWindows.Count;
        var step = 34 * index;
        reminder.WindowStartupLocation = WindowStartupLocation.Manual;
        reminder.Left = Math.Clamp(area.Left + 48 + step, area.Left, Math.Max(area.Left, area.Right - reminder.Width));
        reminder.Top = Math.Clamp(area.Top + 48 + step, area.Top, Math.Max(area.Top, area.Bottom - reminder.Height));
    }

    private void ShowTodoReminderWindow(TodoItem todo, TodoGroupWindow groupWindow)
    {
        if (_todoReminderWindows.TryGetValue(todo.Id, out var existing))
        {
            if (todo.ReminderLevel == ReminderLevel.Ultra)
            {
                Infrastructure.NativeMethods.TryActivate(existing);
            }
            return;
        }

        var groupName = _snapshot.TodoGroups.FirstOrDefault(group => group.Id == todo.GroupId)?.Name ?? "待办";
        var reminder = new ReminderWindow(todo.Title, $"分组：{groupName}", todo.ReminderLevel,
            _snapshot.Settings.EnableMaterial, "待办");
        PlaceReminderWindow(reminder);
        reminder.SnoozeRequested += (_, due) =>
        {
            TodoPlanner.Snooze(todo, due);
            groupWindow.StopReminderSignal();
            groupWindow.RefreshData();;
            MarkDirty();
            _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
        };
        reminder.DismissRequested += _ =>
        {
            TodoPlanner.Dismiss(todo);
            groupWindow.StopReminderSignal();
            groupWindow.RefreshData();;
            MarkDirty();
        };
        reminder.CompleteRequested += _ =>
        {
            TodoPlanner.SetCompleted(todo, true, DateTimeOffset.Now);
            TodoPlanner.CompleteEligibleAncestors(_snapshot.TodoItems, todo, DateTimeOffset.Now, parent =>
                _snapshot.Settings.AutoCompleteParentTodo || TodoDialogs.ConfirmParentCompletion(reminder, parent.Title));
            groupWindow.StopReminderSignal();
            RefreshTodoGroupWindows();
            RefreshManagerIfVisible();
            MarkDirty();
            _reminderScheduler?.Refresh(_snapshot.Notes, _snapshot.TodoItems);
        };
        reminder.Closed += (_, _) => _todoReminderWindows.Remove(todo.Id);
        _todoReminderWindows[todo.Id] = reminder;
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
                if (Environment.GetEnvironmentVariable("PINNOTE_COMPOSITE_CAPTURE") == "1")
                {
                    window.Topmost = true;
                    window.Activate();
                    await Task.Delay(180);
                    VisualCaptureService.CaptureComposited(window, capturePath);
                }
                else
                {
                    VisualCaptureService.Capture(window, capturePath);
                }
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
                    Backdrop = window.MaterialResult,
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
        window.ConfigureMarkdownVisualTest();
        await Task.Delay(140);
        VisualCaptureService.Capture(window, Path.Combine(directory, "note-markdown-preview.png"));

        window.ConfigurePreciseReminderForVisualTest();
        await Task.Delay(120);
        VisualCaptureService.Capture(window, Path.Combine(directory, "precise-time-animation.png"));
        var reminderTooltip = window.OpenReminderTooltipForVisualTest();
        try
        {
            await Task.Delay(100);
            VisualCaptureService.Capture(reminderTooltip, Path.Combine(directory, "reminder-level-tooltip.png"));
        }
        finally
        {
            reminderTooltip.IsOpen = false;
        }

        _snapshot.Settings.RememberFavoriteTextColor("#6B5BD2");
        _snapshot.Settings.RememberFavoriteTextColor("#D68A2D");
        _snapshot.Settings.RememberFavoriteTextColor("#3A86C8");
        window.OpenTextColorPaletteForVisualTest();
        try
        {
            await Task.Delay(150);
            VisualCaptureService.Capture(window.TextColorPaletteVisualForTest, Path.Combine(directory, "text-color-palette.png"));
        }
        finally
        {
            window.CloseTextColorPaletteForVisualTest();
        }

        ReminderStateMachine.Schedule(window.Note, DateTimeOffset.Now.AddMinutes(-8), ReminderLevel.Normal);
        window.ConfigureVisualTest(window.Note.Title, window.GetPlainText(), showReminderEditor: false);
        window.RefreshReminderStatus();
        window.ApplyReminderSignal(ReminderLevel.Normal);
        await Task.Delay(220);
        VisualCaptureService.Capture(window, Path.Combine(directory, "note-overdue.png"));
        window.StopReminderSignal();
        await Task.Delay(80);
        VisualCaptureService.Capture(window, Path.Combine(directory, "note-overdue-static.png"));

        var normalReminderNote = window.Note.Clone();
        normalReminderNote.ReminderLevel = ReminderLevel.Normal;
        var normalReminder = new ReminderWindow(normalReminderNote, window.GetPlainText(), _snapshot.Settings.EnableMaterial);
        PlaceReminderWindow(normalReminder);
        normalReminder.Show();
        await Task.Delay(220);
        VisualCaptureService.Capture(normalReminder, Path.Combine(directory, "normal-reminder.png"));
        normalReminder.CloseWithoutAction();

        var reminderNote = window.Note.Clone();
        reminderNote.ReminderLevel = ReminderLevel.Strong;
        var reminder = new ReminderWindow(reminderNote, window.GetPlainText(), _snapshot.Settings.EnableMaterial);
        reminder.Show();
        await Task.Delay(220);
        VisualCaptureService.Capture(reminder, Path.Combine(directory, "strong-reminder.png"));
        var snoozeMenu = reminder.OpenSnoozeMenuForVisualQa();
        try
        {
            await Task.Delay(120);
            VisualCaptureService.Capture(snoozeMenu, Path.Combine(directory, "reminder-snooze-menu.png"));
        }
        finally
        {
            snoozeMenu.IsOpen = false;
        }
        reminder.CloseWithoutAction();

        var visualUpdate = new UpdateInfo(
            new Version(0, 5, 0),
            UpdateTrust.Channel,
            new Uri("https://github.com/Kratosmax/PinNote/releases/download/v0.5.0/PinNote-0.5.0-Lite-Portable.zip"),
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
        var todoGroup = new TodoGroup { Name = "版本发布待办", SortOrder = 0 };
        var parentTodo = new TodoItem { GroupId = todoGroup.Id, Title = "发布 PinNote 新版本", SortOrder = 0 };
        var childTodo = new TodoItem
        {
            GroupId = todoGroup.Id,
            ParentId = parentTodo.Id,
            Title = "完成安装包回归验证",
            SortOrder = 0,
            ReminderAt = DateTimeOffset.Now.AddMinutes(-18),
            ReminderState = ReminderState.Dismissed
        };
        var grandchildTodo = new TodoItem
        {
            GroupId = todoGroup.Id,
            ParentId = childTodo.Id,
            Title = "在 Windows 11 150% 缩放下检查长文本与滚动",
            SortOrder = 0,
            IsCompleted = true,
            CompletedAt = DateTimeOffset.Now.AddMinutes(-4),
            ReminderAt = DateTimeOffset.Now.AddHours(-1)
        };
        _snapshot.TodoGroups.Add(todoGroup);
        _snapshot.TodoItems.AddRange([parentTodo, childTodo, grandchildTodo]);
        _snapshot.Notes.Add(new NoteDocument
        {
            Title = "已删除的会议记录",
            DeletedAt = DateTimeOffset.Now.AddDays(-2),
            IsHidden = true
        });
        _snapshot.TodoItems.Add(new TodoItem
        {
            GroupId = todoGroup.Id,
            Title = "已删除的测试任务",
            DeletedAt = DateTimeOffset.Now.AddDays(-1)
        });

        SyncTodoGroupWindows();
        var manager = EnsureManagerWindow();
        manager.RefreshAll();
        manager.Show();
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-notes-default.png"));

        manager.SelectNotesForVisualQa();
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-batch-notes.png"));

        manager.ShowTodoModeForVisualQa();
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-todos-default.png"));

        manager.SelectTodosForVisualQa();
        await Task.Delay(320);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-todos-multiselect.png"));

        manager.ShowUnifiedSearchForVisualQa();
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-unified-search.png"));
        manager.Width = manager.MinWidth;
        manager.Height = manager.MinHeight;
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-unified-search-minimum.png"));
        manager.Width = 1040;
        manager.Height = 680;

        manager.ShowReminderCenterForVisualQa();
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-reminder-center.png"));

        manager.ShowRecycleBinForVisualQa();
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-recycle-bin.png"));

        manager.ShowSettingsMode();
        await Task.Delay(180);
        VisualCaptureService.Capture(manager, Path.Combine(directory, "manager-settings.png"));

        var todoWindow = _todoGroupWindows[todoGroup.Id];
        todoGroup.IsHidden = false;
        todoWindow.ShowFromTray(activate: false);
        todoWindow.RefreshData();
        await Task.Delay(180);
        VisualCaptureService.Capture(todoWindow, Path.Combine(directory, "todo-group-window.png"));
        var originalTopmost = todoWindow.Topmost;
        todoWindow.Topmost = true;
        todoWindow.Activate();
        await Task.Delay(100);
        VisualCaptureService.CaptureComposited(todoWindow, Path.Combine(directory, "todo-group-window-composited.png"));
        todoWindow.Topmost = originalTopmost;
        todoWindow.Left += 16;
        todoWindow.Top += 12;
        await Task.Delay(60);
        VisualCaptureService.Capture(todoWindow, Path.Combine(directory, "todo-group-window-after-move.png"));
        todoWindow.EnableMultiSelectForVisualQa();
        await Task.Delay(120);
        VisualCaptureService.Capture(todoWindow, Path.Combine(directory, "todo-group-window-multiselect.png"));

        var todoContextMenu = todoWindow.OpenContextMenuForVisualQa();
        try
        {
            await Task.Delay(140);
            VisualCaptureService.Capture(todoContextMenu, Path.Combine(directory, "todo-group-context-menu.png"));
        }
        finally
        {
            todoContextMenu.IsOpen = false;
        }

        todoWindow.ShowDropTargetForVisualQa();
        await Task.Delay(100);
        VisualCaptureService.Capture(todoWindow, Path.Combine(directory, "todo-group-drop-target.png"));

        _ = Dispatcher.BeginInvoke(() => TodoDialogs.ConfirmParentCompletion(todoWindow, parentTodo.Title));
        await Task.Delay(180);
        var completionDialog = Windows.Cast<Window>().First(item => item.Title == "子待办已全部完成");
        VisualCaptureService.Capture(completionDialog, Path.Combine(directory, "todo-completion-dialog.png"));
        completionDialog.Close();

        CreateNewTodoGroup(todoWindow);
        var newTodoGroup = _snapshot.TodoGroups.Last();
        var newTodoWindow = _todoGroupWindows[newTodoGroup.Id];
        await Task.Delay(140);
        VisualCaptureService.Capture(newTodoWindow, Path.Combine(directory, "todo-group-new-window.png"));
        newTodoWindow.Hide();

        _ = Dispatcher.BeginInvoke(manager.OpenReminderDialogForVisualQa);
        await Task.Delay(220);
        var reminderDialog = Windows.Cast<Window>().First(window => window.Title == "视觉测试：待办提醒");
        VisualCaptureService.Capture(reminderDialog, Path.Combine(directory, "todo-reminder-dialog.png"));
        var datePicker = FindVisualChild<System.Windows.Controls.DatePicker>(reminderDialog)
            ?? throw new InvalidOperationException("视觉测试未找到待办日期选择器。");
        datePicker.IsDropDownOpen = true;
        await Task.Delay(180);
        var popup = datePicker.Template.FindName("PART_Popup", datePicker) as System.Windows.Controls.Primitives.Popup;
        if (popup?.Child is FrameworkElement calendarSurface)
        {
            VisualCaptureService.Capture(calendarSurface, Path.Combine(directory, "todo-reminder-calendar.png"));
        }
        datePicker.IsDropDownOpen = false;
        reminderDialog.Close();
        todoWindow.Hide();
        manager.Hide();

        await CaptureTraySubmenuForVisualQa("便签", Path.Combine(directory, "tray-note-menu.png"));
        await CaptureTraySubmenuForVisualQa("待办", Path.Combine(directory, "tray-todo-menu.png"));
    }

    private async Task CaptureTraySubmenuForVisualQa(string submenuText, string path)
    {
        if (_trayMenu is null) throw new InvalidOperationException("视觉测试未创建托盘菜单。");
        var location = new System.Drawing.Point(
            (int)SystemParameters.WorkArea.Left + 48,
            (int)SystemParameters.WorkArea.Top + 48);
        _trayMenu.Show(location);
        var submenu = _trayMenu.Items.OfType<Forms.ToolStripMenuItem>()
            .Single(item => item.Text == submenuText);
        submenu.ShowDropDown();
        await Task.Delay(160);
        var bounds = System.Drawing.Rectangle.Union(_trayMenu.Bounds, submenu.DropDown.Bounds);
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        _trayMenu.Close();
        await Task.Delay(60);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }
        return null;
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
        foreach (var window in _todoGroupWindows.Values)
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
        foreach (var reminder in _todoReminderWindows.Values.ToArray())
        {
            reminder.CloseWithoutAction();
        }
        foreach (var window in _todoGroupWindows.Values.ToArray())
        {
            window.AllowCloseAndClose();
        }
        foreach (var window in _noteWindows.Values.ToArray())
        {
            window.AllowCloseAndClose();
        }

        _managerWindow?.AllowCloseAndClose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIconImage?.Dispose();
        _trayIconImage = null;
        _reminderScheduler?.Dispose();
        _updateTimer?.Dispose();
        _globalHotkeyService?.Dispose();
        _saveCoordinator?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown();
    }
}
