using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PinNote.Core.Models;
using PinNote.Infrastructure;

namespace PinNote.Windows;

public sealed partial class ManagerWindow : Window
{
    private readonly NoteSnapshot _snapshot;
    private readonly Func<NoteDocument> _createNote;
    private readonly Action<NoteDocument> _openNote;
    private readonly Action<NoteDocument, bool> _setVisibility;
    private readonly Action<NoteDocument> _deleteNote;
    private readonly Action _changed;
    private readonly ObservableCollection<NoteRow> _rows = [];
    private readonly ObservableCollection<GroupChoice> _groupChoices = [];
    private bool _allowClose;

    public ManagerWindow(
        NoteSnapshot snapshot,
        Func<NoteDocument> createNote,
        Action<NoteDocument> openNote,
        Action<NoteDocument, bool> setVisibility,
        Action<NoteDocument> deleteNote,
        Action changed)
    {
        InitializeComponent();
        _snapshot = snapshot;
        _createNote = createNote;
        _openNote = openNote;
        _setVisibility = setVisibility;
        _deleteNote = deleteNote;
        _changed = changed;
        DataContext = this;
        NoteList.ItemsSource = _rows;
        RefreshAll();
    }

    public IReadOnlyList<GroupChoice> GroupChoices => _groupChoices;

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
        => NativeMethods.InstallMessageHook(
            this,
            () => ((App)Application.Current).ShowManager(),
            () => ((App)Application.Current).RefreshRemindersForSystemChange());

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }
        e.Cancel = true;
        Hide();
    }

    public void RefreshAll()
    {
        var selectedFilter = GroupList.SelectedItem as GroupFilter;
        _groupChoices.Clear();
        _groupChoices.Add(new GroupChoice(null, "未分组"));
        foreach (var group in _snapshot.Groups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name))
        {
            _groupChoices.Add(new GroupChoice(group.Id, group.Name));
        }

        GroupList.Items.Clear();
        GroupList.Items.Add(new GroupFilter(null, false, "全部便签"));
        GroupList.Items.Add(new GroupFilter(null, true, "未分组"));
        foreach (var group in _snapshot.Groups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name))
        {
            GroupList.Items.Add(new GroupFilter(group.Id, false, group.Name));
        }
        GroupList.SelectedItem = GroupList.Items.Cast<GroupFilter>().FirstOrDefault(item => item.Id == selectedFilter?.Id && item.IsUngrouped == selectedFilter?.IsUngrouped)
            ?? GroupList.Items[0];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (GroupList.SelectedItem is not GroupFilter filter)
        {
            return;
        }

        var query = SearchBox.Text.Trim();
        var notes = _snapshot.Notes.Where(note =>
            (filter.IsUngrouped ? note.GroupId is null : filter.Id is null || note.GroupId == filter.Id) &&
            (query.Length == 0 || note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             PlainText(note).Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(note => note.ModifiedAt)
            .Select(note => new NoteRow(note, GroupName(note.GroupId), PlainText(note)));

        _rows.Clear();
        foreach (var row in notes)
        {
            _rows.Add(row);
        }
        PageTitle.Text = filter.Name;
        CountText.Text = $"{_rows.Count} 张便签";
    }

    private string GroupName(Guid? groupId) => groupId is null
        ? "未分组"
        : _snapshot.Groups.FirstOrDefault(group => group.Id == groupId)?.Name ?? "未分组";

    private static string PlainText(NoteDocument note)
    {
        if (string.IsNullOrWhiteSpace(note.RtfContent))
        {
            return string.Empty;
        }
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

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void CreateNote_Click(object sender, RoutedEventArgs e)
    {
        var note = _createNote();
        if (GroupList.SelectedItem is GroupFilter { Id: { } groupId })
        {
            note.GroupId = groupId;
            _changed();
        }
        RefreshAll();
    }

    private void OpenNote_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is NoteRow row)
        {
            _openNote(row.Note);
        }
    }

    private void NoteList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NoteList.SelectedItem is NoteRow row)
        {
            _openNote(row.Note);
        }
    }

    private void ToggleVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is NoteRow row)
        {
            _setVisibility(row.Note, row.Note.IsHidden);
            ApplyFilter();
        }
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not NoteRow row)
        {
            return;
        }
        var result = MessageBox.Show(this, $"确定删除“{row.Note.Title}”吗？此操作无法撤销。", "删除便签", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            _deleteNote(row.Note);
            ApplyFilter();
        }
    }

    private void RowGroup_DropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox { DataContext: NoteRow row, SelectedItem: GroupChoice choice })
        {
            row.Note.GroupId = choice.Id;
            row.Note.ModifiedAt = DateTimeOffset.Now;
            _changed();
            ApplyFilter();
        }
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptForName("新建分组", "分组名称", string.Empty);
        if (name is null)
        {
            return;
        }
        var group = new NoteGroup { Name = name, SortOrder = _snapshot.Groups.Count };
        group.Normalize();
        _snapshot.Groups.Add(group);
        _changed();
        RefreshAll();
        GroupList.SelectedItem = GroupList.Items.Cast<GroupFilter>().First(item => item.Id == group.Id);
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupFilter { Id: { } groupId } ||
            _snapshot.Groups.FirstOrDefault(group => group.Id == groupId) is not { } group)
        {
            return;
        }
        var name = PromptForName("重命名分组", "分组名称", group.Name);
        if (name is null)
        {
            return;
        }
        group.Name = name;
        group.Normalize();
        _changed();
        RefreshAll();
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not GroupFilter { Id: { } groupId } ||
            _snapshot.Groups.FirstOrDefault(group => group.Id == groupId) is not { } group)
        {
            return;
        }
        if (MessageBox.Show(this, $"删除分组“{group.Name}”？其中的便签会移到未分组。", "删除分组", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        foreach (var note in _snapshot.Notes.Where(note => note.GroupId == groupId))
        {
            note.GroupId = null;
        }
        _snapshot.Groups.Remove(group);
        _changed();
        RefreshAll();
    }

    private string? PromptForName(string title, string label, string initial)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 360, Height = 170, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new Grid { Margin = new Thickness(18) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 7) };
        var input = new TextBox { Text = initial, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
        Grid.SetRow(input, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Width = 76, Height = 32, IsCancel = true };
        var accept = new Button { Content = "确定", Width = 76, Height = 32, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        accept.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(accept); Grid.SetRow(buttons, 2);
        panel.Children.Add(caption); panel.Children.Add(input); panel.Children.Add(buttons); dialog.Content = panel;
        input.SelectAll(); input.Focus();
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    public sealed record GroupChoice(Guid? Id, string Name);

    private sealed record GroupFilter(Guid? Id, bool IsUngrouped, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed class NoteRow(NoteDocument note, string groupName, string preview)
    {
        public NoteDocument Note { get; } = note;
        public string Title => Note.Title;
        public string Preview { get; } = string.IsNullOrWhiteSpace(preview) ? "暂无正文" : preview.ReplaceLineEndings(" ");
        public string GroupName { get; } = groupName;
        public string VisibilityGlyph => Note.IsHidden ? "\uE890" : "\uE7B3";
        public string ToggleGlyph => Note.IsHidden ? "\uE8A7" : "\uE711";
        public Brush VisibilityBrush => Note.IsHidden ? Brushes.Gray : (Brush)Application.Current.FindResource("PrimaryBrush");
        public string ReminderText => Note.ReminderAt is { } due ? due.LocalDateTime.ToString("MM-dd HH:mm") : "未设置";
    }
}
