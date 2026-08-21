using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PinNote.Core.Models;
using PinNote.Core.Reminders;
using PinNote.Infrastructure;

namespace PinNote.Windows;

public sealed partial class TodoGroupWindow : Window
{
    private static readonly Brush DefaultBorderBrush = new SolidColorBrush(Color.FromRgb(203, 211, 214));
    private static readonly Brush OverdueBorderBrush = new SolidColorBrush(Color.FromRgb(213, 154, 58));
    private readonly TodoGroup _group;
    private readonly NoteSnapshot _snapshot;
    private readonly Func<bool> _materialEnabled;
    private readonly ObservableCollection<TodoWindowRow> _rows = [];
    private readonly HashSet<Guid> _selectedIds = [];
    private readonly HashSet<Guid> _collapsedIds = [];
    private readonly HashSet<Guid> _animateStrikeIds = [];
    private readonly Dictionary<Guid, int> _completionGenerations = [];
    private bool _initializing = true;
    private bool _allowClose;
    private bool _multiSelect;
    private Point _dragStart;
    private TodoWindowRow? _dragRow;
    private TodoWindowRow? _dropRow;
    private DropMode _dropMode;

    public TodoGroupWindow(TodoGroup group, NoteSnapshot snapshot, Func<bool> materialEnabled)
    {
        InitializeComponent();
        _group = group;
        _snapshot = snapshot;
        _materialEnabled = materialEnabled;
        if (Environment.GetEnvironmentVariable("PINNOTE_VISUAL_QA") == "1") ShowInTaskbar = true;
        Left = group.Left; Top = group.Top; Width = Math.Max(group.Width, MinWidth); Height = Math.Max(group.Height, MinHeight);
        EnsureVisibleOnScreen();
        TodoList.ItemsSource = _rows;
        ApplyPinMode();
        RefreshData();
        _initializing = false;
    }

    public TodoGroup Group => _group;
    public event Action<TodoGroupWindow>? Changed;
    public event Action<TodoGroupWindow>? GeometryChanged;
    public event Action<TodoGroupWindow>? HideRequested;
    public event Action<TodoGroupWindow>? OpenManagerRequested;
    public event Action<TodoGroupWindow>? NewGroupRequested;
    internal BackdropResult MaterialResult { get; private set; } = new(false, null, null);

    public void RefreshData()
    {
        GroupTitle.Text = _group.Name;
        var items = _snapshot.TodoItems.Where(item => item.GroupId == _group.Id).ToArray();
        var children = items.Where(item => item.ParentId is not null).GroupBy(item => item.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.SortOrder).ThenBy(item => item.Title).ToArray());
        _selectedIds.IntersectWith(items.Select(item => item.Id));
        _collapsedIds.IntersectWith(items.Select(item => item.Id));
        _rows.Clear();
        foreach (var root in items.Where(item => item.ParentId is null).OrderBy(item => item.SortOrder).ThenBy(item => item.Title)) AppendRows(root, 0, children);
        _animateStrikeIds.Clear();
        StatusText.Text = items.Length == 0 ? "暂无待办" : $"{items.Count(item => !item.IsCompleted)} 项未完成 · {items.Count(item => item.IsCompleted)} 项已完成";
        UpdateSelectionState();
        SetStaticReminderVisual();
    }

    public void RefreshMaterial() => MaterialResult = NativeMethods.ApplyBackdrop(this, FrameBorder, _materialEnabled(), surfaceTintAlpha: 48);

    public void ShowFromTray(bool activate)
    {
        if (!IsVisible && !activate) { ShowActivated = false; Show(); ShowActivated = true; } else Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (activate) Activate();
    }

    public void CaptureGeometry()
    {
        if (WindowState != WindowState.Normal) return;
        _group.Left = Left; _group.Top = Top; _group.Width = ActualWidth > 0 ? ActualWidth : Width; _group.Height = ActualHeight > 0 ? ActualHeight : Height;
    }

    public void ApplyReminderSignal(ReminderLevel level)
    {
        StopReminderSignal();
        var color = level switch { ReminderLevel.Weak => Color.FromRgb(213, 154, 58), ReminderLevel.Normal => Color.FromRgb(224, 126, 48), ReminderLevel.Strong => Color.FromRgb(201, 91, 82), ReminderLevel.Ultra => Color.FromRgb(218, 57, 57), _ => Color.FromRgb(213, 154, 58) };
        var brush = new SolidColorBrush(color); FrameBorder.BorderBrush = brush;
        var duration = TimeSpan.FromMilliseconds(level == ReminderLevel.Weak ? 550 : 280);
        var repeat = level == ReminderLevel.Ultra ? RepeatBehavior.Forever : new RepeatBehavior(level == ReminderLevel.Weak ? 3 : 5);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation { From = color, To = Color.FromArgb(70, color.R, color.G, color.B), Duration = duration, AutoReverse = true, RepeatBehavior = repeat });
        var thickness = new ThicknessAnimation { From = new Thickness(1), To = new Thickness(level == ReminderLevel.Weak ? 2 : 4), Duration = duration, AutoReverse = true, RepeatBehavior = repeat };
        if (level != ReminderLevel.Ultra) thickness.Completed += (_, _) => SetStaticReminderVisual();
        FrameBorder.BeginAnimation(Border.BorderThicknessProperty, thickness);
    }

    public void StopReminderSignal()
    {
        FrameBorder.BeginAnimation(Border.BorderThicknessProperty, null);
        if (FrameBorder.BorderBrush is SolidColorBrush brush) brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        SetStaticReminderVisual();
    }

    public void AllowCloseAndClose() { _allowClose = true; Close(); }

    internal void EnableMultiSelectForVisualQa()
    {
        SetMultiSelect(true);
        foreach (var row in _rows.Take(2)) _selectedIds.Add(row.Item.Id);
        RefreshData();
    }

    internal ContextMenu OpenContextMenuForVisualQa()
    {
        UpdateLayout();
        var border = FindRowBorder(TodoList) ?? throw new InvalidOperationException("视觉测试未找到待办行。");
        var menu = border.ContextMenu ?? throw new InvalidOperationException("视觉测试未找到待办右键菜单。");
        menu.PlacementTarget = border;
        menu.IsOpen = true;
        return menu;
    }

    internal void ShowDropTargetForVisualQa()
    {
        if (_rows.Count > 1) SetDropState(_rows[1], DropMode.Child);
    }

    private static Border? FindRowBorder(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Border { Tag: TodoWindowRow }) return (Border)child;
            if (FindRowBorder(child) is { } found) return found;
        }
        return null;
    }
    private void AppendRows(TodoItem item, int depth, IReadOnlyDictionary<Guid, TodoItem[]> children)
    {
        var hasChildren = children.TryGetValue(item.Id, out var descendants);
        _rows.Add(new TodoWindowRow(item, depth, hasChildren, _collapsedIds.Contains(item.Id), _multiSelect, _selectedIds.Contains(item.Id), _animateStrikeIds.Contains(item.Id)));
        if (!hasChildren || _collapsedIds.Contains(item.Id)) return;
        foreach (var child in descendants!) AppendRows(child, depth + 1, children);
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TodoWindowRow row } checkBox) return;
        var complete = checkBox.IsChecked == true;
        var generation = _completionGenerations.TryGetValue(row.Item.Id, out var previousGeneration)
            ? previousGeneration + 1
            : 1;
        _completionGenerations[row.Item.Id] = generation;
        TodoPlanner.SetCompleted(row.Item, complete, DateTimeOffset.Now);
        if (complete) _animateStrikeIds.Add(row.Item.Id);
        RefreshData();
        Changed?.Invoke(this);
        if (!complete) return;

        await Task.Delay(320);
        if (!_completionGenerations.TryGetValue(row.Item.Id, out var currentGeneration)
            || currentGeneration != generation
            || !_snapshot.TodoItems.Contains(row.Item)
            || !row.Item.IsCompleted)
        {
            return;
        }
        var completedParents = TodoPlanner.CompleteEligibleAncestors(_snapshot.TodoItems, row.Item, DateTimeOffset.Now, parent =>
            _snapshot.Settings.AutoCompleteParentTodo || TodoDialogs.ConfirmParentCompletion(this, parent.Title));
        if (completedParents.Count == 0) return;
        _animateStrikeIds.UnionWith(completedParents.Select(parent => parent.Id));
        RefreshData();
        Changed?.Invoke(this);
    }

    private void AddTodo_Click(object sender, RoutedEventArgs e) => AddTodo();
    private void NewTodoBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { AddTodo(); e.Handled = true; } }
    private void AddTodo()
    {
        var title = NewTodoBox.Text.Trim(); if (title.Length == 0) return;
        _snapshot.TodoItems.Add(new TodoItem { GroupId = _group.Id, Title = title, SortOrder = _snapshot.TodoItems.Count(item => item.GroupId == _group.Id && item.ParentId is null) });
        NewTodoBox.Clear(); NotifyChanged();
    }

    private void GroupTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _group.Name = GroupTitle.Text;
        Changed?.Invoke(this);
    }

    private void GroupTitle_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var title = TodoDialogs.PromptForTitle(this, "重命名待办分组", _group.Name);
        if (title is null) return;
        _group.Name = title;
        GroupTitle.Text = title;
        NotifyChanged();
        e.Handled = true;
    }
    private void TodoTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || ((FrameworkElement)sender).DataContext is not TodoWindowRow row) return;
        var title = TodoDialogs.PromptForTitle(this, "编辑待办", row.Item.Title);
        if (title is null) return;
        row.Item.Title = title;
        NotifyChanged();
        e.Handled = true;
    }

    private void AddChild_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is TodoWindowRow row) AddChild(row); }
    private void ContextAdd_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is TodoWindowRow row) AddChild(row); }
    private void AddChild(TodoWindowRow parent)
    {
        var title = TodoDialogs.PromptForTitle(this, "新增子待办"); if (title is null) return;
        _snapshot.TodoItems.Add(new TodoItem { GroupId = _group.Id, ParentId = parent.Item.Id, Title = title, SortOrder = _snapshot.TodoItems.Count(item => item.ParentId == parent.Item.Id) });
        _collapsedIds.Remove(parent.Item.Id); NotifyChanged();
    }

    private void ContextEdit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TodoWindowRow row) return;
        var title = TodoDialogs.PromptForTitle(this, "编辑待办", row.Item.Title); if (title is null) return;
        row.Item.Title = title; NotifyChanged();
    }

    private void SetReminder_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is TodoWindowRow row) SetReminder(row); }
    private void ContextReminder_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is TodoWindowRow row) SetReminder(row); }
    private void SetReminder(TodoWindowRow row)
    {
        var selection = TodoDialogs.PromptForReminder(this, "设置待办提醒", row.Item.ReminderAt, row.Item.ReminderLevel, true); if (selection is null) return;
        if (selection.Value.Due is { } due) TodoPlanner.Schedule(row.Item, due, selection.Value.Level); else TodoPlanner.ClearReminder(row.Item);
        NotifyChanged();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is TodoWindowRow row) Delete(row); }
    private void ContextDelete_Click(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).DataContext is TodoWindowRow row) Delete(row); }
    private void Delete(TodoWindowRow row)
    {
        var descendants = TodoPlanner.Descendants(_snapshot.TodoItems, row.Item.Id);
        var suffix = descendants.Count == 0 ? string.Empty : $"及其 {descendants.Count} 项子待办";
        if (!TodoDialogs.Confirm(this, "删除待办", $"确定删除“{row.Item.Title}”{suffix}吗？此操作无法撤销。", true)) return;
        var ids = descendants.Select(item => item.Id).Append(row.Item.Id).ToHashSet();
        _snapshot.TodoItems.RemoveAll(item => ids.Contains(item.Id)); _selectedIds.ExceptWith(ids); NotifyChanged();
    }

    private void MultiSelect_Click(object sender, RoutedEventArgs e) => SetMultiSelect(!_multiSelect);
    private void CancelMultiSelect_Click(object sender, RoutedEventArgs e) => SetMultiSelect(false);
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && _multiSelect) { SetMultiSelect(false); e.Handled = true; } }
    private void ContextMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TodoWindowRow row) return;
        SetMultiSelect(true); _selectedIds.Add(row.Item.Id); RefreshData();
    }
    private void SetMultiSelect(bool enabled)
    {
        _multiSelect = enabled; if (!enabled) _selectedIds.Clear();
        BatchBar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        MultiSelectButton.Foreground = enabled ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("TextSecondaryBrush");
        MultiSelectButton.ToolTip = enabled ? "完成多选" : "多选"; RefreshData();
    }
    private void Selection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TodoWindowRow row } box) return;
        if (box.IsChecked == true) _selectedIds.Add(row.Item.Id); else _selectedIds.Remove(row.Item.Id);
        row.IsSelected = box.IsChecked == true; UpdateSelectionState();
    }
    private void BatchReminder_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems(); if (items.Count == 0) return;
        var selection = TodoDialogs.PromptForReminder(this, $"为 {items.Count} 项待办统一设置提醒", items[0].ReminderAt, items[0].ReminderLevel, false);
        if (selection?.Due is not { } due) return;
        foreach (var item in items) TodoPlanner.Schedule(item, due, selection.Value.Level); NotifyChanged();
    }
    private void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var items = SelectedItems(); if (items.Count == 0) return;
        var ids = items.Select(item => item.Id).ToHashSet(); foreach (var item in items) ids.UnionWith(TodoPlanner.Descendants(_snapshot.TodoItems, item.Id).Select(child => child.Id));
        if (!TodoDialogs.Confirm(this, "批量删除待办", $"确定删除选中的待办及其子待办，共 {ids.Count} 项吗？此操作无法撤销。", true)) return;
        _snapshot.TodoItems.RemoveAll(item => ids.Contains(item.Id)); _selectedIds.Clear(); NotifyChanged();
    }
    private List<TodoItem> SelectedItems() => _snapshot.TodoItems.Where(item => _selectedIds.Contains(item.Id)).ToList();
    private void UpdateSelectionState()
    {
        SelectionText.Text = $"已选 {_selectedIds.Count} 项"; BatchReminderButton.IsEnabled = BatchDeleteButton.IsEnabled = _selectedIds.Count > 0;
    }

    private void ToggleExpanded_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not TodoWindowRow row) return;
        if (!_collapsedIds.Add(row.Item.Id)) _collapsedIds.Remove(row.Item.Id); RefreshData();
    }

    private void TodoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(TodoList); _dragRow = RowFromSource(e.OriginalSource as DependencyObject);
    }
    private void TodoList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is null) return;
        var point = e.GetPosition(TodoList);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(TodoList, _dragRow.Item.Id.ToString(), DragDropEffects.Move); _dragRow = null; ClearDropState();
    }
    private void TodoList_DragOver(object sender, DragEventArgs e)
    {
        var row = RowFromSource(e.OriginalSource as DependencyObject);
        if (row is null || !Guid.TryParse(e.Data.GetData(DataFormats.StringFormat) as string, out var draggedId) || draggedId == row.Item.Id) { e.Effects = DragDropEffects.None; ClearDropState(); return; }
        var container = TodoList.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem;
        if (container is null) return;
        var y = e.GetPosition(container).Y; var ratio = y / Math.Max(1, container.ActualHeight);
        var mode = ratio < .25 ? DropMode.Before : ratio > .75 ? DropMode.After : DropMode.Child;
        SetDropState(row, mode); e.Effects = DragDropEffects.Move; e.Handled = true;
    }
    private void TodoList_Drop(object sender, DragEventArgs e)
    {
        if (_dropRow is null || !Guid.TryParse(e.Data.GetData(DataFormats.StringFormat) as string, out var id) || _snapshot.TodoItems.FirstOrDefault(item => item.Id == id) is not { } dragged) { ClearDropState(); return; }
        if (TodoPlanner.Move(_snapshot.TodoItems, dragged, _dropRow.Item, _dropMode == DropMode.Child, _dropMode == DropMode.After)) { if (_dropMode == DropMode.Child) _collapsedIds.Remove(_dropRow.Item.Id); NotifyChanged(); }
        ClearDropState(); e.Handled = true;
    }
    private void TodoList_DragLeave(object sender, DragEventArgs e) { if (!TodoList.IsMouseOver) ClearDropState(); }
    private void SetDropState(TodoWindowRow row, DropMode mode)
    {
        if (_dropRow == row && _dropMode == mode) return; ClearDropState(); _dropRow = row; _dropMode = mode; row.SetDrop(mode);
    }
    private void ClearDropState() { _dropRow?.SetDrop(DropMode.None); _dropRow = null; _dropMode = DropMode.None; }
    private TodoWindowRow? RowFromSource(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem) source = VisualTreeHelper.GetParent(source);
        return (source as ListBoxItem)?.DataContext as TodoWindowRow;
    }

    private void NotifyChanged() { Changed?.Invoke(this); RefreshData(); }
    private void NewGroup_Click(object sender, RoutedEventArgs e) => NewGroupRequested?.Invoke(this);
    private void PinButton_Click(object sender, RoutedEventArgs e) { _group.PinMode = _group.PinMode == PinMode.Desktop ? PinMode.AlwaysOnTop : PinMode.Desktop; ApplyPinMode(); Changed?.Invoke(this); }
    private void ApplyPinMode() { Topmost = _group.PinMode == PinMode.AlwaysOnTop; PinButton.Foreground = _group.PinMode == PinMode.AlwaysOnTop ? (Brush)FindResource("PrimaryBrush") : (Brush)FindResource("TextSecondaryBrush"); PinButton.ToolTip = _group.PinMode == PinMode.AlwaysOnTop ? "已全局置顶，点击切换到桌面模式" : "桌面模式，点击全局置顶"; }
    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this);
    private void OpenManager_Click(object sender, RoutedEventArgs e) => OpenManagerRequested?.Invoke(this);
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    private void DragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Window_SourceInitialized(object? sender, EventArgs e) { RefreshMaterial(); NativeMethods.InstallMessageHook(this, () => ((App)Application.Current).ShowManager(), () => ((App)Application.Current).RefreshRemindersForSystemChange()); }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (_allowClose) return; e.Cancel = true; HideRequested?.Invoke(this); }
    private void Window_GeometryChanged(object? sender, EventArgs e) { if (_initializing || WindowState != WindowState.Normal) return; CaptureGeometry(); GeometryChanged?.Invoke(this); }
    private void EnsureVisibleOnScreen() { var area = SystemParameters.WorkArea; Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)); Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)); }
    private void SetStaticReminderVisual() { var overdue = _snapshot.TodoItems.Any(item => item.GroupId == _group.Id && item.IsOverdue(DateTimeOffset.Now)); FrameBorder.BorderThickness = new Thickness(overdue ? 2 : 1); FrameBorder.BorderBrush = overdue ? OverdueBorderBrush : DefaultBorderBrush; }

    private enum DropMode { None, Before, Child, After }

    private sealed class TodoWindowRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private Brush _dropBackground = Brushes.Transparent;
        private Brush _dropBorder = Brushes.Transparent;
        private Thickness _dropThickness;
        public TodoWindowRow(TodoItem item, int depth, bool hasChildren, bool collapsed, bool multiSelect, bool selected, bool animateStrike) { Item = item; Depth = depth; HasChildren = hasChildren; IsCollapsed = collapsed; MultiSelect = multiSelect; _isSelected = selected; AnimateStrike = animateStrike; }
        public TodoItem Item { get; }
        public int Depth { get; }
        public bool HasChildren { get; }
        public bool IsCollapsed { get; }
        public bool MultiSelect { get; }
        public string Title => Item.Title;
        public bool IsCompleted => Item.IsCompleted;
        public bool AnimateStrike { get; }
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }
        public Thickness Indent => new(Math.Min(Depth, 8) * 14, 0, 0, 0);
        public Visibility SelectionVisibility => MultiSelect ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ExpandVisibility => HasChildren ? Visibility.Visible : Visibility.Hidden;
        public string ExpandGlyph => IsCollapsed ? "\uE76C" : "\uE70D";
        public Visibility StrikeVisibility => Item.IsCompleted ? Visibility.Visible : Visibility.Collapsed;
        public Brush Foreground => Item.IsCompleted ? Resource("TextSecondaryBrush") : Item.IsOverdue(DateTimeOffset.Now) ? Resource("DangerBrush") : Resource("TextPrimaryBrush");
        public string ReminderText => Item.ReminderAt is not { } due ? string.Empty : $"{(Item.IsOverdue(DateTimeOffset.Now) ? "已逾期 · " : string.Empty)}{due.LocalDateTime:MM-dd HH:mm:ss} · {LevelLabel(Item.ReminderLevel)}";
        public Brush ReminderBrush => Item.IsOverdue(DateTimeOffset.Now) ? Resource("DangerBrush") : Resource("TextSecondaryBrush");
        public Brush DropBackground { get => _dropBackground; private set { _dropBackground = value; OnPropertyChanged(); } }
        public Brush DropBorder { get => _dropBorder; private set { _dropBorder = value; OnPropertyChanged(); } }
        public Thickness DropThickness { get => _dropThickness; private set { _dropThickness = value; OnPropertyChanged(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetDrop(DropMode mode)
        {
            DropBackground = mode == DropMode.Child ? new SolidColorBrush(Color.FromArgb(24, 20, 125, 118)) : Brushes.Transparent;
            DropBorder = mode == DropMode.None ? Brushes.Transparent : Resource("PrimaryBrush");
            DropThickness = mode switch { DropMode.Before => new Thickness(0, 2, 0, 0), DropMode.After => new Thickness(0, 0, 0, 2), DropMode.Child => new Thickness(1), _ => new Thickness(0) };
        }
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        private static Brush Resource(string key) => (Brush)Application.Current.FindResource(key);
        private static string LevelLabel(ReminderLevel level) => level switch { ReminderLevel.Weak => "弱提醒", ReminderLevel.Normal => "普通提醒", ReminderLevel.Strong => "强提醒", ReminderLevel.Ultra => "超强提醒", _ => "提醒" };
    }
}
