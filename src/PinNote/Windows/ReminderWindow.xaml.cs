using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PinNote.Core.Models;
using PinNote.Infrastructure;

namespace PinNote.Windows;

public sealed partial class ReminderWindow : Window
{
    private readonly NoteDocument _note;
    private readonly ReminderLevel _level;
    private bool _handled;

    public ReminderWindow(NoteDocument note, string preview, bool materialEnabled)
    {
        InitializeComponent();
        _note = note;
        _level = note.ReminderLevel;
        ShowActivated = _level == ReminderLevel.Ultra;
        LevelText.Text = _level == ReminderLevel.Ultra ? "超强提醒 · 需要处理" : "强提醒 · 已置前";
        TitleText.Text = string.IsNullOrWhiteSpace(note.Title) ? "便签提醒" : note.Title;
        PreviewText.Text = string.IsNullOrWhiteSpace(preview) ? "这张便签设置了提醒。" : preview;
        Tag = materialEnabled;
    }

    public event Action<ReminderWindow>? SnoozeRequested;

    public event Action<ReminderWindow>? DismissRequested;

    public event Action<ReminderWindow>? CompleteRequested;

    public void CloseWithoutAction()
    {
        _handled = true;
        Close();
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

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        _handled = true;
        SnoozeRequested?.Invoke(this);
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
