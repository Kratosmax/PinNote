using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PinNote.Core.Models;
using PinNote.Infrastructure;

namespace PinNote.Windows;

internal readonly record struct TodoReminderSelection(DateTimeOffset? Due, ReminderLevel Level);

internal static class TodoDialogs
{
    public static string? PromptForTitle(Window owner, string title, string initial = "", string label = "待办事项")
    {
        var dialog = Create(owner, title, 520, 300);
        var panel = CreatePanel();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        var input = new TextBox
        {
            Text = initial,
            MinHeight = 110,
            Padding = new Thickness(10, 8, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(input, 1);
        panel.Children.Add(input);
        panel.Children.Add(ButtonRow(dialog, () => !string.IsNullOrWhiteSpace(input.Text), row: 2));
        SetContent(dialog, panel);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
    public static TodoReminderSelection? PromptForReminder(
        Window owner,
        string title,
        DateTimeOffset? initial,
        ReminderLevel initialLevel,
        bool allowClear)
    {
        var value = initial?.LocalDateTime ?? DateTime.Now.AddHours(1);
        var dialog = Create(owner, title, 560, 334);
        var panel = CreatePanel();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        panel.Children.Add(new TextBlock { Text = "提醒时间（精确到秒）", FontWeight = FontWeights.SemiBold });

        var fields = new Grid { Margin = new Thickness(0, 12, 0, 10) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 3; index++) fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        var date = new DatePicker { SelectedDate = value.Date, Height = 36, VerticalContentAlignment = VerticalAlignment.Center };
        date.SetResourceReference(DatePicker.CalendarStyleProperty, "LargeCalendarStyle");
        fields.Children.Add(date);
        var hour = TimeCombo(value.Hour, 24);
        var minute = TimeCombo(value.Minute, 60);
        var second = TimeCombo(value.Second, 60);
        AddTimeField(fields, hour, 1, "时");
        AddTimeField(fields, minute, 2, "分");
        AddTimeField(fields, second, 3, "秒");
        Grid.SetRow(fields, 1);
        panel.Children.Add(fields);

        var levelLabel = new TextBlock { Text = "提醒强度", VerticalAlignment = VerticalAlignment.Center };
        levelLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetRow(levelLabel, 2);
        panel.Children.Add(levelLabel);
        var levels = new UniformGrid { Columns = 2, Rows = 2, Margin = new Thickness(0, 0, 0, 8) };
        var weak = LevelButton("弱提醒", "窗口边框柔和呼吸数秒，不发送系统通知。", initialLevel == ReminderLevel.Weak);
        var normal = LevelButton("普通提醒", "窗口边框闪动，不弹出新的提醒窗口。", initialLevel == ReminderLevel.Normal);
        var strong = LevelButton("强提醒", "打开置顶提醒窗口，但不抢夺键盘焦点。", initialLevel == ReminderLevel.Strong);
        var ultra = LevelButton("超强提醒", "打开置顶大提醒并持续闪动，直到处理。", initialLevel == ReminderLevel.Ultra);
        weak.GroupName = normal.GroupName = strong.GroupName = ultra.GroupName = $"TodoLevel{Guid.NewGuid():N}";
        levels.Children.Add(weak); levels.Children.Add(normal); levels.Children.Add(strong); levels.Children.Add(ultra);
        Grid.SetRow(levels, 3);
        panel.Children.Add(levels);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (allowClear)
        {
            var clear = StyledButton("清除提醒", false);
            clear.Click += (_, _) => { dialog.Tag = new TodoReminderSelection(null, initialLevel); dialog.DialogResult = true; };
            buttons.Children.Add(clear);
        }
        var cancel = StyledButton("取消", false); cancel.IsCancel = true; cancel.Margin = new Thickness(8, 0, 0, 0);
        var accept = StyledButton("确定", true); accept.IsDefault = true; accept.Margin = new Thickness(8, 0, 0, 0);
        accept.Click += (_, _) =>
        {
            if (date.SelectedDate is not { } selectedDate || !TryPart(hour.Text, 23, out var h) ||
                !TryPart(minute.Text, 59, out var m) || !TryPart(second.Text, 59, out var s))
            {
                return;
            }
            var level = weak.IsChecked == true ? ReminderLevel.Weak : strong.IsChecked == true ? ReminderLevel.Strong : ultra.IsChecked == true ? ReminderLevel.Ultra : ReminderLevel.Normal;
            dialog.Tag = new TodoReminderSelection(new DateTimeOffset(new DateTime(selectedDate.Year, selectedDate.Month, selectedDate.Day, h, m, s, DateTimeKind.Local)), level);
            dialog.DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        Grid.SetRow(buttons, 4);
        panel.Children.Add(buttons);
        SetContent(dialog, panel);
        return dialog.ShowDialog() == true && dialog.Tag is TodoReminderSelection selection ? selection : null;
    }

    public static bool Confirm(Window owner, string title, string message, bool danger = false)
    {
        var dialog = Create(owner, title, 440, 215);
        var panel = CreatePanel();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(text);
        var buttons = ButtonRow(dialog, () => true, 1, "取消", danger ? "删除" : "确定");
        if (danger && buttons.Children[1] is Button accept) accept.SetResourceReference(Control.BackgroundProperty, "DangerBrush");
        panel.Children.Add(buttons);
        SetContent(dialog, panel);
        return dialog.ShowDialog() == true;
    }
    public static bool ConfirmParentCompletion(Window owner, string parentTitle)
    {
        var dialog = Create(owner, "子待办已全部完成", 440, 230);
        var panel = CreatePanel();
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        var title = new TextBlock { Text = "恭喜完成这一阶段", FontSize = 18, FontWeight = FontWeights.SemiBold };
        panel.Children.Add(title);
        var message = new TextBlock { Text = $"“{parentTitle}”的所有子待办都已完成。\n是否同时完成父待办？", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) };
        message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        Grid.SetRow(message, 1);
        panel.Children.Add(message);
        panel.Children.Add(ButtonRow(dialog, () => true, 2, "暂不", "完成父待办"));
        SetContent(dialog, panel);
        return dialog.ShowDialog() == true;
    }

    private static Window Create(Window owner, string title, double width, double height)
    {
        var dialog = new Window { Owner = owner, Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, MinWidth = 420, MinHeight = 230, ShowInTaskbar = false };
        dialog.SetResourceReference(Control.BackgroundProperty, "SurfaceRaisedBrush");
        return dialog;
    }

    private static void SetContent(Window dialog, UIElement content)
    {
        dialog.Height += 40;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Background = new SolidColorBrush(Color.FromArgb(12, 0, 0, 0)) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        header.Children.Add(new TextBlock { Text = dialog.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) });
        var close = new Button { Content = "\uE711", ToolTip = "关闭" };
        close.SetResourceReference(FrameworkElement.StyleProperty, "IconButtonStyle");
        close.Click += (_, _) => dialog.Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, args) => { if (args.LeftButton == MouseButtonState.Pressed) dialog.DragMove(); };
        layout.Children.Add(header);
        Grid.SetRow(content, 1);
        layout.Children.Add(content);
        var surface = new Border
        {
            Background = (Brush)Application.Current.FindResource("SurfaceRaisedBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Child = layout
        };
        dialog.Content = surface;
        dialog.Loaded += (_, _) => NativeMethods.ApplyBackdrop(dialog, surface, ((App)Application.Current).MaterialEnabled, 235);
    }
    private static Grid CreatePanel() => new() { Margin = new Thickness(22, 20, 22, 18) };

    private static StackPanel ButtonRow(Window dialog, Func<bool> canAccept, int row, string cancelText = "取消", string acceptText = "确定")
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = StyledButton(cancelText, false); cancel.IsCancel = true;
        var accept = StyledButton(acceptText, true); accept.IsDefault = true; accept.Margin = new Thickness(8, 0, 0, 0);
        accept.Click += (_, _) => { if (canAccept()) dialog.DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(accept);
        Grid.SetRow(buttons, row);
        return buttons;
    }

    private static Button StyledButton(string text, bool primary)
    {
        var button = new Button { Content = text, MinWidth = 82, Height = 34 };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "CommandButtonStyle" : "SecondaryButtonStyle");
        return button;
    }

    private static ComboBox TimeCombo(int selected, int count) => new()
    {
        IsEditable = true,
        Text = selected.ToString("00"),
        ItemsSource = Enumerable.Range(0, count).Select(value => value.ToString("00")),
        Height = 36,
        Padding = new Thickness(8, 0, 2, 0),
        HorizontalContentAlignment = HorizontalAlignment.Left,
        VerticalContentAlignment = VerticalAlignment.Center
    };

    private static void AddTimeField(Grid fields, ComboBox combo, int column, string suffix)
    {
        var grid = new Grid { Margin = new Thickness(6, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(combo, 0);
        grid.Children.Add(combo);
        var label = new TextBlock { Text = suffix, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 4, 0), IsHitTestVisible = false };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Grid.SetColumn(grid, column);
        fields.Children.Add(grid);
    }
    private static RadioButton LevelButton(string text, string toolTip, bool selected)
    {
        var button = new RadioButton { Content = text, ToolTip = toolTip, IsChecked = selected };
        button.SetResourceReference(FrameworkElement.StyleProperty, "ReminderLevelOptionStyle");
        button.Checked += (_, _) =>
        {
            var scale = new ScaleTransform(0.96, 0.96);
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = scale;
            var easing = new BackEase { Amplitude = 0.16, EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
        };
        return button;
    }

    private static bool TryPart(string text, int max, out int value) => int.TryParse(text, out value) && value >= 0 && value <= max;
}
