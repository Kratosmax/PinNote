using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PinNote.Core.Models;
using PinNote.Core.Reminders;
using PinNote.Infrastructure;

namespace PinNote.Windows;

public sealed partial class ManagerWindow : Window
{
    private readonly NoteSnapshot _snapshot;
    private readonly Func<NoteDocument> _createNote;
    private readonly Action<NoteDocument> _openNote;
    private readonly Action<NoteDocument, bool> _setVisibility;
    private readonly Action<NoteDocument> _deleteNote;
    private readonly Func<NoteDocument, NoteDocument> _duplicateNote;
    private readonly Action<NoteDocument> _restoreNote;
    private readonly Action<NoteDocument> _purgeNote;
    private readonly Action<TodoGroup, bool> _setTodoGroupVisibility;
    private readonly Action _changed;
    private readonly Func<SettingsPanel> _createSettingsPanel;
    private readonly ObservableCollection<NoteRow> _noteRows = [];
    private readonly ObservableCollection<TodoRow> _todoRows = [];
    private readonly ObservableCollection<OverviewRow> _overviewRows = [];
    private readonly ObservableCollection<GroupChoice> _groupChoices = [];
    private readonly HashSet<Guid> _selectedNotes = [];
    private readonly HashSet<Guid> _selectedTodos = [];
    private readonly Dictionary<Guid, int> _completionGenerations = [];
    private bool _todoMode;
    private OverviewMode _overviewMode;
    private bool _noteMultiSelect;
    private bool _todoMultiSelect;
    private bool _updatingSelectAll;
    private bool _allowClose;

    public ManagerWindow(NoteSnapshot snapshot, Func<NoteDocument> createNote, Action<NoteDocument> openNote,
        Action<NoteDocument, bool> setVisibility, Action<NoteDocument> deleteNote,
        Func<NoteDocument, NoteDocument> duplicateNote, Action<NoteDocument> restoreNote, Action<NoteDocument> purgeNote,
        Action<TodoGroup, bool> setTodoGroupVisibility, Action changed, Func<SettingsPanel> createSettingsPanel)
    {
        InitializeComponent();
        _snapshot = snapshot;
        _createNote = createNote;
        _openNote = openNote;
        _setVisibility = setVisibility;
        _deleteNote = deleteNote;
        _duplicateNote = duplicateNote;
        _restoreNote = restoreNote;
        _purgeNote = purgeNote;
        _setTodoGroupVisibility = setTodoGroupVisibility;
        _changed = changed;
        _createSettingsPanel = createSettingsPanel;
        DataContext = this;
        NoteList.ItemsSource = _noteRows;
        TodoList.ItemsSource = _todoRows;
        OverviewList.ItemsSource = _overviewRows;
        SearchFilterCombo.ItemsSource = new[]
        {
            new SearchFilterOption(SearchFilter.CurrentView, "当前视图"),
            new SearchFilterOption(SearchFilter.All, "全部"),
            new SearchFilterOption(SearchFilter.Notes, "便签"),
            new SearchFilterOption(SearchFilter.Todos, "待办"),
            new SearchFilterOption(SearchFilter.WithReminder, "有提醒"),
            new SearchFilterOption(SearchFilter.Overdue, "已逾期"),
            new SearchFilterOption(SearchFilter.CompletedTodos, "已完成待办"),
            new SearchFilterOption(SearchFilter.IncompleteTodos, "未完成待办")
        };
        SearchFilterCombo.SelectedIndex = 0;
        BatchGroupCombo.ItemsSource = _groupChoices;
        RefreshAll();
    }

    public IReadOnlyList<GroupChoice> GroupChoices => _groupChoices;

    public Visibility NoteSelectionVisibility => _noteMultiSelect ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TodoSelectionVisibility => _todoMultiSelect ? Visibility.Visible : Visibility.Collapsed;

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    internal void SelectNotesForVisualQa()
    {
        SetNoteMultiSelect(true);
        foreach (var row in _noteRows.Take(2))
        {
            row.IsSelected = true;
            _selectedNotes.Add(row.Note.Id);
        }
        NoteList.Items.Refresh();
        UpdateNoteBatchState();
    }

    internal void ShowTodoModeForVisualQa() => SwitchMode(todoMode: true);
    public void ShowNoteMode() => SwitchMode(todoMode: false);
    public void ShowTodoMode() => SwitchMode(todoMode: true);

    internal void SelectTodosForVisualQa()
    {
        SetTodoMultiSelect(true);
        foreach (var row in _todoRows.Take(2))
        {
            row.IsSelected = true;
            _selectedTodos.Add(row.Item.Id);
        }
        TodoList.Items.Refresh();
        UpdateTodoBatchState();
    }

    internal void OpenReminderDialogForVisualQa() =>
        TryPromptForReminder("视觉测试：待办提醒", DateTimeOffset.Now.AddHours(1), ReminderLevel.Strong,
            true, out _, out _);

    internal void ShowUnifiedSearchForVisualQa()
    {
        SearchBox.Text = string.Empty;
        SearchFilterCombo.SelectedIndex = 1;
        ShowOverview(OverviewMode.UnifiedSearch);
    }

    internal void ShowReminderCenterForVisualQa()
    {
        SearchBox.Text = string.Empty;
        ShowOverview(OverviewMode.ReminderCenter);
    }

    internal void ShowRecycleBinForVisualQa()
    {
        SearchBox.Text = string.Empty;
        ShowOverview(OverviewMode.RecycleBin);
    }

    public void ShowSettingsMode() => SettingsMode_Click(this, new RoutedEventArgs());
    private void SettingsMode_Click(object sender, RoutedEventArgs e)
    {
        _overviewMode = OverviewMode.None;
        _todoMode = false;
        NoteModeButton.IsChecked = false; TodoModeButton.IsChecked = false; SettingsModeButton.IsChecked = true;
        NotePanel.Visibility = Visibility.Collapsed; TodoPanel.Visibility = Visibility.Collapsed;
        OverviewPanel.Visibility = Visibility.Collapsed; SettingsPanelControl.Visibility = Visibility.Visible;
        GroupList.Visibility = Visibility.Collapsed; SettingsNavPanel.Visibility = Visibility.Visible;
        OverviewNavPanel.Visibility = Visibility.Collapsed; AddGroupButton.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed; SearchFilterCombo.Visibility = Visibility.Collapsed;
        TodoWindowButton.Visibility = Visibility.Collapsed; CreateItemButton.Visibility = Visibility.Collapsed;
        PageTitle.Text = "设置";
        var panel = _createSettingsPanel();
        panel.CancelRequested += (_, _) => SwitchMode(todoMode: false);
        SettingsPanelControl.Content = panel;
    }
    private void SettingsNavGeneral_Click(object sender, RoutedEventArgs e) => ScrollSettings("general");
    private void SettingsNavShortcuts_Click(object sender, RoutedEventArgs e) => ScrollSettings("shortcuts");
    private void SettingsNavNetwork_Click(object sender, RoutedEventArgs e) => ScrollSettings("network");
    private void SettingsNavUpdates_Click(object sender, RoutedEventArgs e) => ScrollSettings("updates");
    private void ScrollSettings(string section)
    {
        if (SettingsPanelControl.Content is SettingsPanel panel)
        {
            panel.ScrollToSection(section);
        }
    }
    private void Window_SourceInitialized(object? sender, EventArgs e) =>
        NativeMethods.InstallMessageHook(this, () => ((App)Application.Current).ShowManager(),
            () => ((App)Application.Current).RefreshRemindersForSystemChange());

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    public void RefreshAll()
    {
        if (_overviewMode != OverviewMode.None)
        {
            RefreshOverview();
            return;
        }
        var selectedId = (GroupList.SelectedItem as GroupFilter)?.Id;
        BuildGroupList(selectedId);
        ApplyFilter();
    }

    private void BuildGroupList(Guid? selectedId)
    {
        GroupList.Items.Clear();
        _groupChoices.Clear();
        if (_todoMode)
        {
            foreach (var group in _snapshot.TodoGroups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name))
                GroupList.Items.Add(new GroupFilter(group.Id, false, group.Name));
            GroupList.SelectedItem = GroupList.Items.Cast<GroupFilter>().FirstOrDefault(item => item.Id == selectedId)
                ?? GroupList.Items.Cast<GroupFilter>().FirstOrDefault();
            return;
        }

        _groupChoices.Add(new GroupChoice(null, "\u672a\u5206\u7ec4"));
        foreach (var group in _snapshot.Groups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name))
            _groupChoices.Add(new GroupChoice(group.Id, group.Name));

        GroupList.Items.Add(new GroupFilter(null, false, "\u5168\u90e8\u4fbf\u7b7e"));
        GroupList.Items.Add(new GroupFilter(null, true, "\u672a\u5206\u7ec4"));
        foreach (var group in _snapshot.Groups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name))
            GroupList.Items.Add(new GroupFilter(group.Id, false, group.Name));
        GroupList.SelectedItem = GroupList.Items.Cast<GroupFilter>()
            .FirstOrDefault(item => item.Id == selectedId && !item.IsUngrouped) ?? GroupList.Items[0];
        BatchGroupCombo.SelectedIndex = 0;
    }

    private void ApplyFilter()
    {
        if (_todoMode) ApplyTodoFilter(); else ApplyNoteFilter();
    }

    private void ApplyNoteFilter()
    {
        if (GroupList.SelectedItem is not GroupFilter filter) return;
        var query = SearchBox.Text.Trim();
        var notes = _snapshot.Notes.Where(note =>
            note.DeletedAt is null &&
            (filter.IsUngrouped ? note.GroupId is null : filter.Id is null || note.GroupId == filter.Id) &&
            (query.Length == 0 || note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             PlainText(note).Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(note => note.ModifiedAt).ToArray();

        _selectedNotes.IntersectWith(notes.Select(note => note.Id));
        _noteRows.Clear();
        foreach (var note in notes)
            _noteRows.Add(new NoteRow(note, PlainText(note), _selectedNotes.Contains(note.Id)));
        PageTitle.Text = filter.Name;
        NoteCountText.Text = $"{_noteRows.Count} \u5f20\u4fbf\u7b7e";
        UpdateNoteBatchState();
    }

    private void ApplyTodoFilter()
    {
        if (GroupList.SelectedItem is not GroupFilter { Id: { } groupId } filter)
        {
            _todoRows.Clear();
            PageTitle.Text = "\u5f85\u529e";
            TodoCountText.Text = "\u8bf7\u5148\u65b0\u5efa\u5f85\u529e\u5206\u7ec4";
            UpdateTodoBatchState();
            return;
        }

        var items = _snapshot.TodoItems.Where(item => item.GroupId == groupId && item.DeletedAt is null).ToArray();
        var query = SearchBox.Text.Trim();
        HashSet<Guid>? visible = null;
        if (query.Length > 0)
        {
            visible = items.Where(item => item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Select(item => item.Id).ToHashSet();
            var byId = items.ToDictionary(item => item.Id);
            foreach (var id in visible.ToArray())
            {
                var parentId = byId[id].ParentId;
                while (parentId is { } current && byId.TryGetValue(current, out var parent) && visible.Add(current))
                    parentId = parent.ParentId;
            }
        }

        _selectedTodos.IntersectWith(visible ?? items.Select(item => item.Id));
        _todoRows.Clear();
        var children = items.Where(item => item.ParentId is not null)
            .GroupBy(item => item.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SortOrder).ThenBy(item => item.Title).ToArray());
        foreach (var root in items.Where(item => item.ParentId is null).OrderBy(item => item.SortOrder).ThenBy(item => item.Title))
            AppendTodoRows(root, 0, children, visible);

        PageTitle.Text = filter.Name;
        TodoCountText.Text = $"{items.Length} \u9879\u5f85\u529e \u00b7 \u5df2\u5b8c\u6210 {items.Count(item => item.IsCompleted)} \u9879";
        UpdateTodoBatchState();
    }

    private void AppendTodoRows(TodoItem item, int depth, IReadOnlyDictionary<Guid, TodoItem[]> children, IReadOnlySet<Guid>? visible)
    {
        if (visible is null || visible.Contains(item.Id))
            _todoRows.Add(new TodoRow(item, depth, _selectedTodos.Contains(item.Id)));
        if (!children.TryGetValue(item.Id, out var descendants)) return;
        foreach (var child in descendants) AppendTodoRows(child, depth + 1, children, visible);
    }

    private static string PlainText(NoteDocument note)
    {
        if (string.IsNullOrWhiteSpace(note.RtfContent)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(note.RtfContent);
            var document = new FlowDocument();
            var range = new TextRange(document.ContentStart, document.ContentEnd);
            using var stream = new MemoryStream(bytes);
            range.Load(stream, DataFormats.Rtf);
            return range.Text.Trim();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return note.RtfContent;
        }
    }

    private void NoteMode_Click(object sender, RoutedEventArgs e) => SwitchMode(false);
    private void TodoMode_Click(object sender, RoutedEventArgs e) => SwitchMode(true);

    private void SwitchMode(bool todoMode)
    {
        _overviewMode = OverviewMode.None;
        SettingsModeButton.IsChecked = false; SettingsPanelControl.Visibility = Visibility.Collapsed;
        SettingsNavPanel.Visibility = Visibility.Collapsed; OverviewNavPanel.Visibility = Visibility.Visible;
        OverviewPanel.Visibility = Visibility.Collapsed; GroupList.Visibility = Visibility.Visible;
        AddGroupButton.Visibility = Visibility.Visible; SearchBox.Visibility = Visibility.Visible;
        SearchFilterCombo.Visibility = Visibility.Visible; CreateItemButton.Visibility = Visibility.Visible;
        _todoMode = todoMode;
        NoteModeButton.IsChecked = !todoMode;
        TodoModeButton.IsChecked = todoMode;
        NotePanel.Visibility = todoMode ? Visibility.Collapsed : Visibility.Visible;
        TodoPanel.Visibility = todoMode ? Visibility.Visible : Visibility.Collapsed;
        TodoWindowButton.Visibility = todoMode ? Visibility.Visible : Visibility.Collapsed;
        if (SearchFilterCombo.SelectedItem is not SearchFilterOption { Filter: SearchFilter.CurrentView })
            SearchFilterCombo.SelectedIndex = 0;
        if (todoMode) SetNoteMultiSelect(false); else SetTodoMultiSelect(false);
        CreateItemButton.Content = todoMode ? "+ 新建待办" : "+ 新建便签";
        AddGroupButton.Content = todoMode ? "+ 待办分组" : "+ 新建分组";
        SearchBox.ToolTip = todoMode ? "搜索待办标题" : "搜索标题或正文";
        if (todoMode && _snapshot.TodoGroups.Count == 0)
        {
            _snapshot.TodoGroups.Add(new TodoGroup { Name = "我的待办" });
            _changed();
        }
        RefreshAll();
    }

    private void SearchFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SearchFilterCombo.SelectedItem is not SearchFilterOption option) return;
        if (option.Filter == SearchFilter.CurrentView)
        {
            if (_overviewMode == OverviewMode.UnifiedSearch) SwitchMode(_todoMode);
            else ApplyFilter();
            return;
        }
        ShowOverview(OverviewMode.UnifiedSearch);
    }

    private void ReminderCenter_Click(object sender, RoutedEventArgs e) => ShowOverview(OverviewMode.ReminderCenter);
    private void RecycleBin_Click(object sender, RoutedEventArgs e) => ShowOverview(OverviewMode.RecycleBin);

    private void ShowOverview(OverviewMode mode)
    {
        _overviewMode = mode;
        NoteModeButton.IsChecked = false; TodoModeButton.IsChecked = false; SettingsModeButton.IsChecked = false;
        NotePanel.Visibility = Visibility.Collapsed; TodoPanel.Visibility = Visibility.Collapsed;
        SettingsPanelControl.Visibility = Visibility.Collapsed; OverviewPanel.Visibility = Visibility.Visible;
        GroupList.Visibility = Visibility.Collapsed; SettingsNavPanel.Visibility = Visibility.Collapsed;
        OverviewNavPanel.Visibility = Visibility.Visible; AddGroupButton.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Visible;
        SearchFilterCombo.Visibility = mode == OverviewMode.UnifiedSearch ? Visibility.Visible : Visibility.Collapsed;
        TodoWindowButton.Visibility = Visibility.Collapsed; CreateItemButton.Visibility = Visibility.Collapsed;
        PageTitle.Text = mode switch
        {
            OverviewMode.ReminderCenter => "提醒中心",
            OverviewMode.RecycleBin => "回收站",
            _ => "统一搜索"
        };
        SearchBox.ToolTip = "搜索便签和待办";
        RefreshOverview();
    }

    private void RefreshOverview()
    {
        _overviewRows.Clear();
        var now = DateTimeOffset.Now;
        var query = SearchBox.Text.Trim();
        var filter = (SearchFilterCombo.SelectedItem as SearchFilterOption)?.Filter ?? SearchFilter.All;

        if (_overviewMode == OverviewMode.RecycleBin)
        {
            foreach (var note in _snapshot.Notes.Where(note => note.DeletedAt is not null && Matches(note.Title, PlainText(note), query))
                         .OrderByDescending(note => note.DeletedAt))
                _overviewRows.Add(OverviewRow.ForTrash(note, $"删除于 {note.DeletedAt!.Value.LocalDateTime:yyyy-MM-dd HH:mm}"));
            foreach (var todo in _snapshot.TodoItems.Where(item => item.DeletedAt is not null && Matches(item.Title, string.Empty, query))
                         .OrderByDescending(item => item.DeletedAt))
                _overviewRows.Add(OverviewRow.ForTrash(todo, $"删除于 {todo.DeletedAt!.Value.LocalDateTime:yyyy-MM-dd HH:mm}"));
        }
        else
        {
            var notes = _snapshot.Notes.Where(note => note.DeletedAt is null && Matches(note.Title, PlainText(note), query));
            var todos = _snapshot.TodoItems.Where(item => item.DeletedAt is null && Matches(item.Title, string.Empty, query));
            if (_overviewMode == OverviewMode.ReminderCenter)
            {
                notes = notes.Where(note => note.ReminderAt is not null);
                todos = todos.Where(item => item.ReminderAt is not null && !item.IsCompleted);
            }
            else
            {
                notes = filter switch
                {
                    SearchFilter.Todos or SearchFilter.CompletedTodos or SearchFilter.IncompleteTodos => [],
                    SearchFilter.WithReminder => notes.Where(note => note.ReminderAt is not null),
                    SearchFilter.Overdue => notes.Where(note => note.IsOverdue(now)),
                    _ => notes
                };
                todos = filter switch
                {
                    SearchFilter.Notes => [],
                    SearchFilter.WithReminder => todos.Where(item => item.ReminderAt is not null),
                    SearchFilter.Overdue => todos.Where(item => item.IsOverdue(now)),
                    SearchFilter.CompletedTodos => todos.Where(item => item.IsCompleted),
                    SearchFilter.IncompleteTodos => todos.Where(item => !item.IsCompleted),
                    _ => todos
                };
            }

            foreach (var note in notes.OrderByDescending(note => note.ReminderAt ?? note.ModifiedAt))
                _overviewRows.Add(OverviewRow.ForActive(note, PlainText(note), ReminderStatus(note.ReminderAt, note.IsOverdue(now))));
            foreach (var todo in todos.OrderByDescending(item => item.ReminderAt ?? item.CompletedAt ?? DateTimeOffset.MinValue))
            {
                var groupName = _snapshot.TodoGroups.FirstOrDefault(group => group.Id == todo.GroupId)?.Name ?? "待办";
                var status = todo.IsCompleted ? "已完成" : ReminderStatus(todo.ReminderAt, todo.IsOverdue(now));
                _overviewRows.Add(OverviewRow.ForActive(todo, groupName, status));
            }
        }

        OverviewCountText.Text = $"{_overviewRows.Count} 项";
    }

    private static bool Matches(string title, string body, string query) =>
        query.Length == 0 || title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        body.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string ReminderStatus(DateTimeOffset? due, bool overdue) =>
        due is null ? "未设置提醒" : $"{(overdue ? "已逾期 · " : string.Empty)}{due.Value.LocalDateTime:MM-dd HH:mm:ss}";

    private void OverviewOpen_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not OverviewRow row) return;
        if (row.Note is { } note) _openNote(note);
        if (row.Todo is { } todo && _snapshot.TodoGroups.FirstOrDefault(group => group.Id == todo.GroupId) is { } group)
            _setTodoGroupVisibility(group, true);
    }

    private void OverviewDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not OverviewRow row) return;
        if (row.Note is { } note) _duplicateNote(note);
        else if (row.Todo is { } todo) { ItemLifecycle.DuplicateTodoTree(_snapshot.TodoItems, todo); _changed(); }
        RefreshOverview();
    }

    private void OverviewRestore_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not OverviewRow row) return;
        if (row.Note is { } note) _restoreNote(note);
        else if (row.Todo is { } todo) { ItemLifecycle.RestoreTodoTree(_snapshot, todo); _changed(); }
        RefreshOverview();
    }

    private void OverviewPurge_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not OverviewRow row ||
            MessageBox.Show(this, $"永久删除“{row.Title}”？此操作无法撤销。", "永久删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (row.Note is { } note) _purgeNote(note);
        else if (row.Todo is { } todo)
        {
            var ids = TodoPlanner.Descendants(_snapshot.TodoItems, todo.Id, includeDeleted: true)
                .Select(item => item.Id).Append(todo.Id).ToHashSet();
            _snapshot.TodoItems.RemoveAll(item => ids.Contains(item.Id));
            _changed();
        }
        RefreshOverview();
    }

    private void ShowTodoWindow_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is GroupFilter { Id: { } groupId } &&
            _snapshot.TodoGroups.FirstOrDefault(group => group.Id == groupId) is { } group)
        {
            _setTodoGroupVisibility(group, true);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { if (_noteMultiSelect) SetNoteMultiSelect(false); if (_todoMultiSelect) SetTodoMultiSelect(false); } }

    private void ToggleNoteMultiSelect_Click(object sender, RoutedEventArgs e) =>
        SetNoteMultiSelect(!_noteMultiSelect);

    private void ToggleTodoMultiSelect_Click(object sender, RoutedEventArgs e) =>
        SetTodoMultiSelect(!_todoMultiSelect);

    private void SetNoteMultiSelect(bool enabled)
    {
        _noteMultiSelect = enabled;
        NoteBatchActions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        NoteMultiSelectButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (!enabled)
        {
            _selectedNotes.Clear();
            foreach (var row in _noteRows) row.IsSelected = false;
        }
        NoteList.Items.Refresh();
        UpdateNoteBatchState();
    }

    private void SetTodoMultiSelect(bool enabled)
    {
        _todoMultiSelect = enabled;
        TodoBatchActions.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        TodoMultiSelectButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (!enabled)
        {
            _selectedTodos.Clear();
            foreach (var row in _todoRows) row.IsSelected = false;
        }
        TodoList.Items.Refresh();
        UpdateTodoBatchState();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { if (_overviewMode == OverviewMode.None) ApplyFilter(); else RefreshOverview(); }

    private void CreateItem_Click(object sender, RoutedEventArgs e)
    {
        if (_todoMode)
        {
            if (GroupList.SelectedItem is not GroupFilter { Id: { } groupId }) return;
            var title = PromptForName("\u65b0\u5efa\u5f85\u529e", "\u5f85\u529e\u4e8b\u9879", string.Empty);
            if (title is null) return;
            _snapshot.TodoItems.Add(new TodoItem
            {
                GroupId = groupId,
                Title = title,
                SortOrder = _snapshot.TodoItems.Count(item => item.GroupId == groupId && item.ParentId is null)
            });
            _changed();
            ApplyTodoFilter();
            return;
        }

        var note = _createNote();
        if (GroupList.SelectedItem is GroupFilter { Id: { } noteGroupId })
        {
            note.GroupId = noteGroupId;
            _changed();
        }
        RefreshAll();
    }

    private void OpenNote_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is NoteRow row) _openNote(row.Note);
    }

    private void NoteList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NoteList.SelectedItem is NoteRow row) _openNote(row.Note);
    }

    private void ToggleVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not NoteRow row) return;
        _setVisibility(row.Note, row.Note.IsHidden);
        ApplyNoteFilter();
    }

    private void DuplicateNote_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is NoteRow row) _duplicateNote(row.Note);
        RefreshAll();
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not NoteRow row) return;
        if (MessageBox.Show(this, $"\u786e\u5b9a\u5220\u9664\u201c{row.Note.Title}\u201d\u5417\uff1f\u6b64\u64cd\u4f5c\u65e0\u6cd5\u64a4\u9500\u3002", "\u5220\u9664\u4fbf\u7b7e",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _deleteNote(row.Note);
        _selectedNotes.Remove(row.Note.Id);
        ApplyNoteFilter();
    }

    private void RowGroup_DropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox { DataContext: NoteRow row, SelectedItem: GroupChoice choice }) return;
        row.Note.GroupId = choice.Id;
        row.Note.ModifiedAt = DateTimeOffset.Now;
        _changed();
        ApplyNoteFilter();
    }

    private void NoteSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: NoteRow row } checkBox) return;
        row.IsSelected = checkBox.IsChecked == true;
        SetMembership(_selectedNotes, row.Note.Id, row.IsSelected);
        UpdateNoteBatchState();
    }

    private void SelectAllNotes_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSelectAll) return;
        var selected = SelectAllNotesBox.IsChecked == true;
        foreach (var row in _noteRows)
        {
            row.IsSelected = selected;
            SetMembership(_selectedNotes, row.Note.Id, selected);
        }
        NoteList.Items.Refresh();
        UpdateNoteBatchState();
    }

    private void BatchMoveNotes_Click(object sender, RoutedEventArgs e)
    {
        if (BatchGroupCombo.SelectedItem is not GroupChoice choice) return;
        foreach (var note in SelectedNotes())
        {
            note.GroupId = choice.Id;
            note.ModifiedAt = DateTimeOffset.Now;
        }
        _changed();
        ApplyNoteFilter();
    }

    private void BatchShowNotes_Click(object sender, RoutedEventArgs e) => SetSelectedNotesVisibility(true);
    private void BatchHideNotes_Click(object sender, RoutedEventArgs e) => SetSelectedNotesVisibility(false);

    private void SetSelectedNotesVisibility(bool visible)
    {
        foreach (var note in SelectedNotes()) _setVisibility(note, visible);
        ApplyNoteFilter();
    }

    private void BatchDeleteNotes_Click(object sender, RoutedEventArgs e)
    {
        var notes = SelectedNotes();
        if (notes.Count == 0 || MessageBox.Show(this, $"\u786e\u5b9a\u5220\u9664\u9009\u4e2d\u7684 {notes.Count} \u5f20\u4fbf\u7b7e\u5417\uff1f\u6b64\u64cd\u4f5c\u65e0\u6cd5\u64a4\u9500\u3002",
                "\u6279\u91cf\u5220\u9664\u4fbf\u7b7e", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var note in notes)
        {
            _deleteNote(note);
            _selectedNotes.Remove(note.Id);
        }
        ApplyNoteFilter();
    }

    private List<NoteDocument> SelectedNotes() => _snapshot.Notes.Where(note => note.DeletedAt is null && _selectedNotes.Contains(note.Id)).ToList();

    private void UpdateNoteBatchState()
    {
        var count = _noteRows.Count(row => row.IsSelected);
        NoteSelectionText.Text = $"\u5df2\u9009 {count} \u9879";
        BatchMoveButton.IsEnabled = BatchShowButton.IsEnabled = BatchHideButton.IsEnabled = BatchDeleteNotesButton.IsEnabled = count > 0;
        _updatingSelectAll = true;
        SelectAllNotesBox.IsChecked = _noteRows.Count > 0 && count == _noteRows.Count;
        _updatingSelectAll = false;
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForName(_todoMode ? "\u65b0\u5efa\u5f85\u529e\u5206\u7ec4" : "\u65b0\u5efa\u5206\u7ec4", "\u5206\u7ec4\u540d\u79f0", string.Empty);
        if (name is null) return;
        Guid id;
        if (_todoMode)
        {
            var group = new TodoGroup { Name = name, SortOrder = _snapshot.TodoGroups.Count };
            group.Normalize();
            _snapshot.TodoGroups.Add(group);
            id = group.Id;
        }
        else
        {
            var group = new NoteGroup { Name = name, SortOrder = _snapshot.Groups.Count };
            group.Normalize();
            _snapshot.Groups.Add(group);
            id = group.Id;
        }
        _changed();
        RefreshAll();
        GroupList.SelectedItem = GroupList.Items.Cast<GroupFilter>().First(item => item.Id == id);
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupFilter { Id: { } groupId }) return;
        if (_todoMode)
        {
            var group = _snapshot.TodoGroups.First(item => item.Id == groupId);
            var name = PromptForName("\u91cd\u547d\u540d\u5f85\u529e\u5206\u7ec4", "\u5206\u7ec4\u540d\u79f0", group.Name);
            if (name is null) return;
            group.Name = name;
            group.Normalize();
        }
        else
        {
            var group = _snapshot.Groups.First(item => item.Id == groupId);
            var name = PromptForName("\u91cd\u547d\u540d\u5206\u7ec4", "\u5206\u7ec4\u540d\u79f0", group.Name);
            if (name is null) return;
            group.Name = name;
            group.Normalize();
        }
        _changed();
        RefreshAll();
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupFilter { Id: { } groupId }) return;
        if (_todoMode)
        {
            var group = _snapshot.TodoGroups.First(item => item.Id == groupId);
            var count = _snapshot.TodoItems.Count(item => item.GroupId == groupId);
            if (MessageBox.Show(this, $"\u5220\u9664\u5f85\u529e\u5206\u7ec4\u201c{group.Name}\u201d\u53ca\u5176\u4e2d {count} \u9879\u5f85\u529e\uff1f\u6b64\u64cd\u4f5c\u65e0\u6cd5\u64a4\u9500\u3002",
                    "\u5220\u9664\u5f85\u529e\u5206\u7ec4", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (var item in _snapshot.TodoItems.Where(item => item.GroupId == groupId && item.DeletedAt is null).ToArray())
                ItemLifecycle.MoveTodoTreeToTrash(_snapshot.TodoItems, item, DateTimeOffset.Now);
            _snapshot.TodoGroups.Remove(group);
            _selectedTodos.Clear();
        }
        else
        {
            var group = _snapshot.Groups.First(item => item.Id == groupId);
            if (MessageBox.Show(this, $"\u5220\u9664\u5206\u7ec4\u201c{group.Name}\u201d\uff1f\u5176\u4e2d\u7684\u4fbf\u7b7e\u4f1a\u79fb\u5230\u672a\u5206\u7ec4\u3002", "\u5220\u9664\u5206\u7ec4",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var note in _snapshot.Notes.Where(note => note.GroupId == groupId)) note.GroupId = null;
            _snapshot.Groups.Remove(group);
        }
        _changed();
        RefreshAll();
    }

    private async void TodoComplete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TodoRow row } checkBox) return;
        var complete = checkBox.IsChecked == true;
        var generation = _completionGenerations.TryGetValue(row.Item.Id, out var previousGeneration)
            ? previousGeneration + 1
            : 1;
        _completionGenerations[row.Item.Id] = generation;
        TodoPlanner.SetCompleted(row.Item, complete, DateTimeOffset.Now);
        row.RefreshStatus();
        _changed();
        if (complete)
        {
            await Task.Delay(320);
            if (!_completionGenerations.TryGetValue(row.Item.Id, out var currentGeneration)
                || currentGeneration != generation
                || !_snapshot.TodoItems.Contains(row.Item)
                || !row.Item.IsCompleted)
            {
                return;
            }
            CompleteEligibleAncestors(row.Item);
        }
        ApplyTodoFilter();
    }

    private void CompleteEligibleAncestors(TodoItem item)
    {
        var completed = TodoPlanner.CompleteEligibleAncestors(_snapshot.TodoItems, item, DateTimeOffset.Now, parent =>
            _snapshot.Settings.AutoCompleteParentTodo || TodoDialogs.ConfirmParentCompletion(this, parent.Title));
        if (completed.Count > 0)
        {
            _changed();
        }
    }
    private void AddChildTodo_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TodoRow parent) return;
        var title = PromptForName("\u65b0\u589e\u5b50\u5f85\u529e", "\u5f85\u529e\u4e8b\u9879", string.Empty);
        if (title is null) return;
        _snapshot.TodoItems.Add(new TodoItem
        {
            GroupId = parent.Item.GroupId,
            ParentId = parent.Item.Id,
            Title = title,
            SortOrder = _snapshot.TodoItems.Count(item => item.ParentId == parent.Item.Id)
        });
        _changed();
        ApplyTodoFilter();
    }

    private void RenameTodo_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TodoRow row) return;
        var title = PromptForName("\u91cd\u547d\u540d\u5f85\u529e", "\u5f85\u529e\u4e8b\u9879", row.Item.Title);
        if (title is null) return;
        row.Item.Title = title.Trim();
        _changed();
        ApplyTodoFilter();
    }

    private void SetTodoReminder_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TodoRow row ||
            !TryPromptForReminder("设置待办提醒", row.Item.ReminderAt, row.Item.ReminderLevel,
                true, out var due, out var level)) return;
        if (due is { } value) TodoPlanner.Schedule(row.Item, value, level); else TodoPlanner.ClearReminder(row.Item);
        _changed();
        ApplyTodoFilter();
    }
    private void DuplicateTodo_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TodoRow row) return;
        ItemLifecycle.DuplicateTodoTree(_snapshot.TodoItems, row.Item);
        _changed();
        ApplyTodoFilter();
    }

    private void DeleteTodo_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TodoRow row) return;
        var descendants = TodoPlanner.Descendants(_snapshot.TodoItems, row.Item.Id);
        var suffix = descendants.Count == 0 ? string.Empty : $"\u53ca\u5176 {descendants.Count} \u9879\u5b50\u5f85\u529e";
        if (MessageBox.Show(this, $"\u786e\u5b9a\u5220\u9664\u201c{row.Item.Title}\u201d{suffix}\u5417\uff1f\u6b64\u64cd\u4f5c\u65e0\u6cd5\u64a4\u9500\u3002", "\u5220\u9664\u5f85\u529e",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ids = ItemLifecycle.MoveTodoTreeToTrash(_snapshot.TodoItems, row.Item, DateTimeOffset.Now);
        _selectedTodos.ExceptWith(ids);
        _changed();
        ApplyTodoFilter();
    }

    private void TodoSelection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TodoRow row } checkBox) return;
        row.IsSelected = checkBox.IsChecked == true;
        SetMembership(_selectedTodos, row.Item.Id, row.IsSelected);
        UpdateTodoBatchState();
    }

    private void SelectAllTodos_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSelectAll) return;
        var selected = SelectAllTodosBox.IsChecked == true;
        foreach (var row in _todoRows)
        {
            row.IsSelected = selected;
            SetMembership(_selectedTodos, row.Item.Id, selected);
        }
        TodoList.Items.Refresh();
        UpdateTodoBatchState();
    }

    private void BatchReminderTodos_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedTodos();
        if (items.Count == 0 || !TryPromptForReminder($"为 {items.Count} 项待办统一设置提醒",
                items[0].ReminderAt, items[0].ReminderLevel, false, out var due, out var level) ||
            due is not { } value) return;
        foreach (var item in items) TodoPlanner.Schedule(item, value, level);
        _changed();
        ApplyTodoFilter();
    }
    private void BatchClearReminderTodos_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedTodos();
        if (items.Count == 0) return;
        foreach (var item in items) TodoPlanner.ClearReminder(item);
        _changed();
        ApplyTodoFilter();
    }

    private void BatchDeleteTodos_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedTodos();
        if (selected.Count == 0) return;
        var ids = selected.Select(item => item.Id).ToHashSet();
        foreach (var item in selected)
            ids.UnionWith(TodoPlanner.Descendants(_snapshot.TodoItems, item.Id).Select(descendant => descendant.Id));
        if (MessageBox.Show(this, $"\u786e\u5b9a\u5220\u9664\u9009\u4e2d\u7684\u5f85\u529e\u53ca\u5176\u5b50\u5f85\u529e\uff0c\u5171 {ids.Count} \u9879\u5417\uff1f\u6b64\u64cd\u4f5c\u65e0\u6cd5\u64a4\u9500\u3002",
                "\u6279\u91cf\u5220\u9664\u5f85\u529e", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var item in selected) ItemLifecycle.MoveTodoTreeToTrash(_snapshot.TodoItems, item, DateTimeOffset.Now);
        _selectedTodos.ExceptWith(ids);
        _changed();
        ApplyTodoFilter();
    }

    private List<TodoItem> SelectedTodos() => _snapshot.TodoItems.Where(item => item.DeletedAt is null && _selectedTodos.Contains(item.Id)).ToList();

    private void UpdateTodoBatchState()
    {
        var count = _todoRows.Count(row => row.IsSelected);
        TodoSelectionText.Text = $"\u5df2\u9009 {count} \u9879";
        BatchReminderButton.IsEnabled = BatchClearReminderButton.IsEnabled = BatchDeleteTodosButton.IsEnabled = count > 0;
        _updatingSelectAll = true;
        SelectAllTodosBox.IsChecked = _todoRows.Count > 0 && count == _todoRows.Count;
        _updatingSelectAll = false;
    }

    private static void SetMembership(HashSet<Guid> ids, Guid id, bool selected)
    {
        if (selected) ids.Add(id); else ids.Remove(id);
    }

    private string? PromptForName(string title, string label, string initial) => TodoDialogs.PromptForTitle(this, title, initial, label);

    private bool TryPromptForReminder(
        string title,
        DateTimeOffset? initial,
        ReminderLevel initialLevel,
        bool allowClear,
        out DateTimeOffset? due,
        out ReminderLevel level)
    {
        var result = TodoDialogs.PromptForReminder(this, title, initial, initialLevel, allowClear);
        if (result is null)
        {
            due = null;
            level = initialLevel;
            return false;
        }

        due = result.Value.Due;
        level = result.Value.Level;
        return true;
    }
    public sealed record GroupChoice(Guid? Id, string Name);

    private sealed record GroupFilter(Guid? Id, bool IsUngrouped, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed class NoteRow(NoteDocument note, string preview, bool selected)
    {
        public NoteDocument Note { get; } = note;
        public bool IsSelected { get; set; } = selected;
        public string Title => Note.Title;
        public string Preview { get; } = string.IsNullOrWhiteSpace(preview) ? "\u6682\u65e0\u6b63\u6587" : preview.ReplaceLineEndings(" ");
        public string VisibilityGlyph => Note.IsHidden ? "\uE890" : "\uE7B3";
        public string ToggleGlyph => Note.IsHidden ? "\uE8A7" : "\uE711";
        public Brush VisibilityBrush => Note.IsHidden ? Brushes.Gray : (Brush)Application.Current.FindResource("PrimaryBrush");
        public string ReminderText => Note.ReminderAt is not { } due ? "\u672a\u8bbe\u7f6e"
            : due <= DateTimeOffset.Now ? $"\u5df2\u903e\u671f \u00b7 {due.LocalDateTime:MM-dd HH:mm:ss}" : due.LocalDateTime.ToString("MM-dd HH:mm:ss");
        public Brush ReminderBrush => Note.ReminderAt is { } due && due <= DateTimeOffset.Now
            ? (Brush)Application.Current.FindResource("DangerBrush")
            : (Brush)Application.Current.FindResource("TextSecondaryBrush");
    }

    private enum OverviewMode { None, UnifiedSearch, ReminderCenter, RecycleBin }
    private enum SearchFilter { CurrentView, All, Notes, Todos, WithReminder, Overdue, CompletedTodos, IncompleteTodos }
    private sealed record SearchFilterOption(SearchFilter Filter, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed class OverviewRow
    {
        private OverviewRow(string kind, string title, string subtitle, string status, NoteDocument? note, TodoItem? todo, bool trash)
        {
            Kind = kind; Title = title; Subtitle = subtitle; Status = status; Note = note; Todo = todo; IsTrash = trash;
        }

        public string Kind { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public string Status { get; }
        public NoteDocument? Note { get; }
        public TodoItem? Todo { get; }
        public bool IsTrash { get; }
        public Brush StatusBrush => Status.StartsWith("已逾期", StringComparison.Ordinal)
            ? (Brush)Application.Current.FindResource("DangerBrush")
            : (Brush)Application.Current.FindResource("TextSecondaryBrush");
        public Visibility OpenVisibility => IsTrash ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DuplicateVisibility => IsTrash ? Visibility.Collapsed : Visibility.Visible;
        public Visibility RestoreVisibility => IsTrash ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PurgeVisibility => IsTrash ? Visibility.Visible : Visibility.Collapsed;

        public static OverviewRow ForActive(NoteDocument note, string subtitle, string status) =>
            new("便签", note.Title, string.IsNullOrWhiteSpace(subtitle) ? "暂无正文" : subtitle.ReplaceLineEndings(" "), status, note, null, false);
        public static OverviewRow ForActive(TodoItem todo, string subtitle, string status) =>
            new("待办", todo.Title, subtitle, status, null, todo, false);
        public static OverviewRow ForTrash(NoteDocument note, string status) =>
            new("便签", note.Title, "可恢复", status, note, null, true);
        public static OverviewRow ForTrash(TodoItem todo, string status) =>
            new("待办", todo.Title, "包含其子级时会一并恢复", status, null, todo, true);
    }
    private sealed class TodoRow : INotifyPropertyChanged
    {
        public TodoRow(TodoItem item, int depth, bool selected)
        {
            Item = item;
            Depth = depth;
            IsSelected = selected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public TodoItem Item { get; }
        public int Depth { get; }
        public Thickness Indent => new(Math.Min(Depth, 10) * 22, 0, 0, 0);
        public string Title => Item.Title;
        public bool IsSelected { get; set; }
        public bool IsCompleted => Item.IsCompleted;
        public Brush Foreground => Item.IsCompleted
            ? (Brush)Application.Current.FindResource("TextSecondaryBrush")
            : Item.IsOverdue(DateTimeOffset.Now)
                ? (Brush)Application.Current.FindResource("DangerBrush")
                : (Brush)Application.Current.FindResource("TextPrimaryBrush");
        public string ReminderText => Item.ReminderAt is not { } due ? "未设置"
            : $"{(Item.IsOverdue(DateTimeOffset.Now) ? "已逾期 · " : string.Empty)}{due.LocalDateTime:MM-dd HH:mm:ss} · {LevelLabel(Item.ReminderLevel)}";
        public Brush ReminderBrush => Item.IsOverdue(DateTimeOffset.Now)
            ? (Brush)Application.Current.FindResource("DangerBrush")
            : (Brush)Application.Current.FindResource("TextSecondaryBrush");

        private static string LevelLabel(ReminderLevel level) => level switch
        {
            ReminderLevel.Weak => "弱提醒",
            ReminderLevel.Normal => "普通提醒",
            ReminderLevel.Strong => "强提醒",
            ReminderLevel.Ultra => "超强提醒",
            _ => "提醒"
        };

        public void RefreshStatus()
        {
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(Foreground));
            OnPropertyChanged(nameof(ReminderText));
            OnPropertyChanged(nameof(ReminderBrush));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
