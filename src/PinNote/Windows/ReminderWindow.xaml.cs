using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PinNote.Core.Models;
using PinNote.Core.Reminders;
using PinNote.Infrastructure;

namespace PinNote.Windows;

public sealed partial class ReminderWindow : Window
{
    private readonly ReminderLevel _level;
    private bool _handled;

    public ReminderWindow(NoteDocument note, string preview, bool materialEnabled)
        : this(note.Title, preview, note.ReminderLevel, materialEnabled, "便签")
    {
    }

    public ReminderWindow(string title, string preview, ReminderLevel level, bool materialEnabled, string itemType)
    {
        InitializeComponent();
        _level = level;
        ShowActivated = _level == ReminderLevel.Ultra;
        LevelText.Text = _level switch
        {
            ReminderLevel.Normal => "普通提醒 · 已到时间",
            ReminderLevel.Ultra => "超强提醒 · 需要处理",
            _ => "强提醒 · 已置前"
        };
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? $"{itemType}提醒" : title;
        PreviewText.Text = string.IsNullOrWhiteSpace(preview) ? $"这项{itemType}设置了提醒。" : preview;
        Tag = materialEnabled;
    }

    public event Action<ReminderWindow, DateTimeOffset>? SnoozeRequested;

    public event Action<ReminderWindow>? DismissRequested;

    public event Action<ReminderWindow>? CompleteRequested;

    public void CloseWithoutAction()
    {
        _handled = true;
        Close();
    }

    internal ContextMenu OpenSnoozeMenuForVisualQa()
    {
        var menu = SnoozeButton.ContextMenu ?? throw new InvalidOperationException("视觉测试未找到稍后提醒菜单。");
        menu.PlacementTarget = SnoozeButton;
        menu.IsOpen = true;
        return menu;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        NativeMethods.ApplyBackdrop(this, FrameBorder, Tag is true);
        StartAnimation();
        if (_level == ReminderLevel.Ultra)
        {
            NativeMethods.TryActivate(this);
        }
        else
        {
            NativeMethods.ShowWithoutActivation(this, PinMode.AlwaysOnTop);
        }
    }

    private void StartAnimation()
    {
        var brush = new SolidColorBrush(Color.FromRgb(218, 57, 57));
        FrameBorder.BorderBrush = brush;
        var repeat = _level == ReminderLevel.Ultra ? RepeatBehavior.Forever : new RepeatBehavior(7);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            From = Color.FromRgb(218, 57, 57),
            To = Color.FromRgb(255, 191, 70),
            Duration = TimeSpan.FromMilliseconds(320),
            AutoReverse = true,
            RepeatBehavior = repeat
        });
        FrameBorder.BeginAnimation(Border.BorderThicknessProperty, new ThicknessAnimation
        {
            From = new Thickness(3),
            To = new Thickness(7),
            Duration = TimeSpan.FromMilliseconds(320),
            AutoReverse = true,
            RepeatBehavior = repeat
        });
    }

    private void SnoozeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SnoozeButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = SnoozeButton;
        menu.IsOpen = true;
    }

    private void SnoozePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } ||
            !Enum.TryParse<SnoozePreset>(value, out var preset))
        {
            return;
        }

        _handled = true;
        SnoozeRequested?.Invoke(this, SnoozePlanner.GetDue(preset, DateTimeOffset.Now));
        Close();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        _handled = true;
        DismissRequested?.Invoke(this);
        Close();
    }

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        _handled = true;
        CompleteRequested?.Invoke(this);
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_handled)
        {
            _handled = true;
            DismissRequested?.Invoke(this);
        }
    }
}
