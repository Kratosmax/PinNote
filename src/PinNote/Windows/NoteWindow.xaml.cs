using System.IO;
using System.Text;
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

public sealed partial class NoteWindow : Window
{
    private static readonly System.Windows.Media.Brush DefaultBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 211, 214));
    private static readonly System.Windows.Media.Brush OverdueBorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(213, 154, 58));
    private readonly NoteDocument _note;
    private readonly Func<bool> _materialEnabled;
    private readonly Func<IReadOnlyList<string>> _favoriteTextColors;
    private bool _initializing = true;
    private bool _suppressSelectionAnimation;
    private bool _allowClose;
    private bool _markdownPreview;
    private bool _suppressEditorPersistence;
    private string _markdownSource = string.Empty;

    public NoteWindow(NoteDocument note, Func<bool> materialEnabled, Func<IReadOnlyList<string>> favoriteTextColors)
    {
        InitializeComponent();
        _note = note;
        _materialEnabled = materialEnabled;
        _favoriteTextColors = favoriteTextColors;
        if (Environment.GetEnvironmentVariable("PINNOTE_VISUAL_QA") == "1")
        {
            ShowInTaskbar = true;
        }

        ReminderHour.ItemsSource = Enumerable.Range(0, 24).Select(value => value.ToString("00")).ToArray();
        ReminderMinute.ItemsSource = Enumerable.Range(0, 60).Select(value => value.ToString("00")).ToArray();
        ReminderSecond.ItemsSource = Enumerable.Range(0, 60).Select(value => value.ToString("00")).ToArray();
        LoadFromModel();
        RefreshFavoriteTextColors();
        _initializing = false;
    }

    public NoteDocument Note => _note;

    public event Action<NoteWindow>? Changed;

    public event Action<NoteWindow>? ReminderChanged;

    public event Action<NoteWindow>? NewRequested;

    public event Action<NoteWindow>? DeleteRequested;

    public event Action<NoteWindow>? DuplicateRequested;

    public event Action<NoteWindow>? HideRequested;

    public event Action<string>? FavoriteTextColorAdded;

    private void LoadFromModel()
    {
        TitleBox.Text = _note.Title;
        Left = _note.Left;
        Top = _note.Top;
        Width = _note.Width;
        Height = _note.Height;
        ApplyPinMode();

        if (!string.IsNullOrWhiteSpace(_note.RtfContent))
        {
            try
            {
                var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(_note.RtfContent);
                }
                catch (FormatException)
                {
                    bytes = Encoding.UTF8.GetBytes(_note.RtfContent);
                }
                using var stream = new MemoryStream(bytes);
                range.Load(stream, DataFormats.Rtf);
            }
            catch (ArgumentException)
            {
                Editor.Document.Blocks.Clear();
                Editor.Document.Blocks.Add(new Paragraph(new Run(_note.RtfContent)));
            }
        }

        EnsureVisibleOnScreen();
        PopulateReminderEditor();
        RefreshReminderStatus();
    }

    private void EnsureVisibleOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void PopulateReminderEditor()
    {
        _suppressSelectionAnimation = true;
        try
        {
            var due = _note.ReminderAt?.LocalDateTime ?? TrimMilliseconds(DateTime.Now.AddHours(1));
            ReminderDate.SelectedDate = due.Date;
            ReminderHour.Text = due.Hour.ToString("00");
            ReminderMinute.Text = due.Minute.ToString("00");
            ReminderSecond.Text = due.Second.ToString("00");
            LevelWeak.IsChecked = _note.ReminderLevel == ReminderLevel.Weak;
            LevelNormal.IsChecked = _note.ReminderLevel == ReminderLevel.Normal;
            LevelStrong.IsChecked = _note.ReminderLevel == ReminderLevel.Strong;
            LevelUltra.IsChecked = _note.ReminderLevel == ReminderLevel.Ultra;
        }
        finally
        {
            _suppressSelectionAnimation = false;
        }
    }

    public string GetPlainText()
    {
        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        return range.Text.ReplaceLineEndings("\n").TrimEnd('\r', '\n');
    }

    public void ShowFromTray(bool activate)
    {
        if (!IsVisible && !activate)
        {
            ShowActivated = false;
            Show();
            ShowActivated = true;
        }
        else
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (activate)
        {
            Activate();
        }
    }

    internal BackdropResult MaterialResult { get; private set; } = new(false, null, null);

    public void RefreshMaterial() =>
        MaterialResult = NativeMethods.ApplyBackdrop(this, FrameBorder, _materialEnabled(), surfaceTintAlpha: 36);

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    public void CaptureGeometry()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }
        _note.Left = Left;
        _note.Top = Top;
        _note.Width = ActualWidth > 0 ? ActualWidth : Width;
        _note.Height = ActualHeight > 0 ? ActualHeight : Height;
    }

    public void ApplyReminderSignal(ReminderLevel level)
    {
        StopReminderSignal();
        var color = level switch
        {
            ReminderLevel.Weak => System.Windows.Media.Color.FromRgb(213, 154, 58),
            ReminderLevel.Normal => System.Windows.Media.Color.FromRgb(224, 126, 48),
            ReminderLevel.Strong => System.Windows.Media.Color.FromRgb(201, 91, 82),
            ReminderLevel.Ultra => System.Windows.Media.Color.FromRgb(218, 57, 57),
            _ => System.Windows.Media.Color.FromRgb(213, 154, 58)
        };
        var brush = new SolidColorBrush(color);
        FrameBorder.BorderBrush = brush;

        var repeat = level == ReminderLevel.Ultra ? RepeatBehavior.Forever : new RepeatBehavior(level == ReminderLevel.Weak ? 3 : 5);
        var colorAnimation = new ColorAnimation
        {
            From = color,
            To = System.Windows.Media.Color.FromArgb(70, color.R, color.G, color.B),
            Duration = TimeSpan.FromMilliseconds(level == ReminderLevel.Weak ? 550 : 280),
            AutoReverse = true,
            RepeatBehavior = repeat
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimation);

        var thicknessAnimation = new ThicknessAnimation
        {
            From = new Thickness(1),
            To = new Thickness(level == ReminderLevel.Weak ? 2 : 4),
            Duration = colorAnimation.Duration,
            AutoReverse = true,
            RepeatBehavior = repeat
        };
        if (level != ReminderLevel.Ultra)
        {
            thicknessAnimation.Completed += (_, _) => SetStaticReminderVisual();
        }

        FrameBorder.BeginAnimation(Border.BorderThicknessProperty, thicknessAnimation);
    }

    public void StopReminderSignal()
    {
        FrameBorder.BeginAnimation(Border.BorderThicknessProperty, null);
        if (FrameBorder.BorderBrush is SolidColorBrush brush)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        }
        SetStaticReminderVisual();
    }

    public void RefreshReminderStatus()
    {
        if (_note.ReminderAt is not { } due)
        {
            ReminderStatus.Text = "未设置提醒";
            SetStaticReminderVisual();
            return;
        }

        var level = LevelLabel(_note.ReminderLevel);
        if (_note.IsOverdue(DateTimeOffset.Now))
        {
            ReminderStatus.Text = $"已逾期 · {due.LocalDateTime:MM-dd HH:mm:ss} · {level}";
        }
        else
        {
            ReminderStatus.Text = $"{due.LocalDateTime:MM-dd HH:mm:ss} · {level}";
        }
        SetStaticReminderVisual();
    }

    public void SetSaveError(bool hasError) => SaveStatus.Text = hasError ? "保存失败" : string.Empty;

    internal void ConfigureVisualTest(string title, string body, bool showReminderEditor)
    {
        TitleBox.Text = title;
        Editor.Document.Blocks.Clear();
        Editor.Document.Blocks.Add(new Paragraph(new Run(body)));
        ReminderPanel.Visibility = showReminderEditor ? Visibility.Visible : Visibility.Collapsed;
        if (showReminderEditor)
        {
            PopulateReminderEditor();
        }
    }

    private void SetStaticReminderVisual()
    {
        FrameBorder.BorderThickness = new Thickness(_note.IsOverdue(DateTimeOffset.Now) ? 2 : 1);
        FrameBorder.BorderBrush = _note.IsOverdue(DateTimeOffset.Now) ? OverdueBorderBrush : DefaultBorderBrush;
    }

    private void ApplyPinMode()
    {
        Topmost = _note.PinMode == PinMode.AlwaysOnTop;
        PinButton.Foreground = _note.PinMode == PinMode.AlwaysOnTop
            ? (System.Windows.Media.Brush)FindResource("PrimaryBrush")
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        PinButton.ToolTip = _note.PinMode == PinMode.AlwaysOnTop ? "已全局置顶，点击切换到桌面模式" : "桌面模式，点击全局置顶";
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        RefreshMaterial();
        NativeMethods.InstallMessageHook(
            this,
            () => ((App)Application.Current).ShowManager(),
            () => ((App)Application.Current).RefreshRemindersForSystemChange());
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => RefreshReminderStatus();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void Window_GeometryChanged(object? sender, EventArgs e)
    {
        if (_initializing || WindowState != WindowState.Normal)
        {
            return;
        }

        _note.Left = Left;
        _note.Top = Top;
        _note.Width = ActualWidth;
        _note.Height = ActualHeight;
        Changed?.Invoke(this);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox)
        {
            DragMove();
        }
    }

    private void DragGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void NewNote_Click(object sender, RoutedEventArgs e) => NewRequested?.Invoke(this);

    private void DuplicateNote_Click(object sender, RoutedEventArgs e) => DuplicateRequested?.Invoke(this);

    private void HideNote_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke(this);

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _note.PinMode = _note.PinMode == PinMode.Desktop ? PinMode.AlwaysOnTop : PinMode.Desktop;
        ApplyPinMode();
        Changed?.Invoke(this);
    }

    private void ToggleReminderPanel_Click(object sender, RoutedEventArgs e)
    {
        ReminderPanel.Visibility = ReminderPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (ReminderPanel.Visibility == Visibility.Visible)
        {
            PopulateReminderEditor();
        }
    }

    private void SetReminder_Click(object sender, RoutedEventArgs e)
    {
        if (ReminderDate.SelectedDate is not { } date ||
            !TryParseTimePart(ReminderHour.Text, 23, out var hour) ||
            !TryParseTimePart(ReminderMinute.Text, 59, out var minute) ||
            !TryParseTimePart(ReminderSecond.Text, 59, out var second))
        {
            MessageBox.Show(this, "请输入有效的日期以及 00-23 时、00-59 分、00-59 秒。", "时间无效",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var local = date.Date
            .AddHours(hour)
            .AddMinutes(minute)
            .AddSeconds(second);
        ReminderStateMachine.Schedule(_note, new DateTimeOffset(local), SelectedReminderLevel());
        ReminderPanel.Visibility = Visibility.Collapsed;
        RefreshReminderStatus();
        ReminderChanged?.Invoke(this);
    }

    private void QuickReminder_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        var due = (((FrameworkElement)sender).Tag as string) switch
        {
            "hour" => TrimMilliseconds(now.AddHours(1)),
            "today" when now.TimeOfDay < TimeSpan.FromHours(18) => now.Date.AddHours(18),
            "today" => now.Date.AddDays(1).AddHours(18),
            "tomorrow" => now.Date.AddDays(1).AddHours(9),
            _ => TrimMilliseconds(now.AddHours(1))
        };
        _suppressSelectionAnimation = true;
        try
        {
            ReminderDate.SelectedDate = due.Date;
            ReminderHour.Text = due.Hour.ToString("00");
            ReminderMinute.Text = due.Minute.ToString("00");
            ReminderSecond.Text = due.Second.ToString("00");
        }
        finally
        {
            _suppressSelectionAnimation = false;
        }
        AnimateTimeSelection();
    }

    private ReminderLevel SelectedReminderLevel()
    {
        if (LevelWeak.IsChecked == true) return ReminderLevel.Weak;
        if (LevelStrong.IsChecked == true) return ReminderLevel.Strong;
        if (LevelUltra.IsChecked == true) return ReminderLevel.Ultra;
        return ReminderLevel.Normal;
    }

    private void ClearReminder_Click(object sender, RoutedEventArgs e)
    {
        ReminderStateMachine.Complete(_note);
        ReminderPanel.Visibility = Visibility.Collapsed;
        StopReminderSignal();
        RefreshReminderStatus();
        ReminderChanged?.Invoke(this);
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, "删除后会移入回收站。确定删除这张便签吗？", "删除便签", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            DeleteRequested?.Invoke(this);
        }
    }

    internal void ConfigureMarkdownVisualTest()
    {
        Editor.Document.Blocks.Clear();
        Editor.Document.Blocks.Add(new Paragraph(new Run("# 发布清单\n\n- **安装包**\n- 自动更新\n\n## 验证结果")));
        ReminderPanel.Visibility = Visibility.Collapsed;
        _markdownPreview = false;
        Markdown_Click(MarkdownButton, new RoutedEventArgs());
    }
    private void Markdown_Click(object sender, RoutedEventArgs e)
    {
        if (!_markdownPreview)
        {
            _markdownSource = GetPlainText();
            _markdownPreview = true;
            MarkdownButton.Content = "编辑";
            MarkdownButton.ToolTip = "返回 Markdown 编辑模式";
            _suppressEditorPersistence = true;
            try { Editor.Document = RenderMarkdown(_markdownSource); Editor.IsReadOnly = true; }
            finally { _suppressEditorPersistence = false; }
        }
        else
        {
            _markdownPreview = false;
            MarkdownButton.Content = "MD";
            MarkdownButton.ToolTip = "Markdown 编辑/预览";
            _suppressEditorPersistence = true;
            try { Editor.Document = new FlowDocument(new Paragraph(new Run(_markdownSource))); Editor.IsReadOnly = false; }
            finally { _suppressEditorPersistence = false; }
        }
    }

    private static FlowDocument RenderMarkdown(string markdown)
    {
        var document = new FlowDocument { PagePadding = new Thickness(4) };
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal)) document.Blocks.Add(new Paragraph(ParseInline(line[2..])) { FontSize = 22, FontWeight = FontWeights.Bold });
            else if (line.StartsWith("## ", StringComparison.Ordinal)) document.Blocks.Add(new Paragraph(ParseInline(line[3..])) { FontSize = 19, FontWeight = FontWeights.SemiBold });
            else if (line.StartsWith("### ", StringComparison.Ordinal)) document.Blocks.Add(new Paragraph(ParseInline(line[4..])) { FontSize = 17, FontWeight = FontWeights.SemiBold });
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) document.Blocks.Add(new Paragraph(ParseInline("• " + line[2..])) { Margin = new Thickness(12, 2, 0, 2) });
            else document.Blocks.Add(new Paragraph(ParseInline(line)));
        }
        return document;
    }

    private static Inline ParseInline(string text)
    {
        var span = new Span();
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf("**", index, StringComparison.Ordinal);
            if (start < 0) { span.Inlines.Add(new Run(text[index..])); break; }
            if (start > index) span.Inlines.Add(new Run(text[index..start]));
            var end = text.IndexOf("**", start + 2, StringComparison.Ordinal);
            if (end < 0) { span.Inlines.Add(new Run(text[start..])); break; }
            span.Inlines.Add(new Bold(new Run(text[(start + 2)..end])));
            index = end + 2;
        }
        return span;
    }
    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _note.Title = TitleBox.Text;
        Changed?.Invoke(this);
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || _suppressEditorPersistence)
        {
            return;
        }

        var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Rtf);
        _note.RtfContent = Convert.ToBase64String(stream.ToArray());
        Changed?.Invoke(this);
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        BoldButton.IsChecked = HasSelectionValue(TextElement.FontWeightProperty, FontWeights.Bold);
        ItalicButton.IsChecked = HasSelectionValue(TextElement.FontStyleProperty, FontStyles.Italic);
        UnderlineButton.IsChecked = HasUnderline();
    }

    private bool HasUnderline()
    {
        if (Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) is not TextDecorationCollection decorations)
        {
            return false;
        }
        return decorations.Any(decoration => decoration.Location == TextDecorationLocation.Underline);
    }
    private bool HasSelectionValue(DependencyProperty property, object expected)
    {
        var value = Editor.Selection.GetPropertyValue(property);
        return value != DependencyProperty.UnsetValue && Equals(value, expected);
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleBold.Execute(null, Editor);

    private void Italic_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleItalic.Execute(null, Editor);

    private void Underline_Click(object sender, RoutedEventArgs e) => EditingCommands.ToggleUnderline.Execute(null, Editor);

    private void InkDark_Click(object sender, RoutedEventArgs e) => ApplyInk(System.Windows.Media.Color.FromRgb(32, 36, 40));

    private void InkTeal_Click(object sender, RoutedEventArgs e) => ApplyInk(System.Windows.Media.Color.FromRgb(20, 125, 118));

    private void InkCoral_Click(object sender, RoutedEventArgs e) => ApplyInk(System.Windows.Media.Color.FromRgb(201, 91, 82));

    private void TextColorMenu_Click(object sender, RoutedEventArgs e)
    {
        RefreshFavoriteTextColors();
        TextColorPopup.IsOpen = !TextColorPopup.IsOpen;
    }

    private void ChooseTextColor_Click(object sender, RoutedEventArgs e) => ChooseTextColor();

    private void ChooseTextColor()
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true
        };
        var owner = new Win32DialogOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var color = System.Windows.Media.Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
        ApplyInk(color);
        FavoriteTextColorAdded?.Invoke($"#{color.R:X2}{color.G:X2}{color.B:X2}");
        RefreshFavoriteTextColors();
        TextColorPopup.IsOpen = false;
    }

    public void RefreshFavoriteTextColors()
    {
        FavoriteColorPanel.Children.Clear();
        var colors = _favoriteTextColors();
        for (var index = 0; index < 3; index++)
        {
            var button = new Button
            {
                Width = 40,
                Height = 32,
                Margin = new Thickness(2),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            if (index < colors.Count &&
                System.Windows.Media.ColorConverter.ConvertFromString(colors[index]) is System.Windows.Media.Color color)
            {
                button.Background = new SolidColorBrush(color);
                button.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, color.R, color.G, color.B));
                button.Tag = color;
                button.ToolTip = $"常用颜色 {colors[index]}";
                button.Click += FavoriteTextColor_Click;
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.BorderBrush = (Brush)FindResource("BorderBrush");
                button.Content = "+";
                button.FontSize = 18;
                button.Foreground = (Brush)FindResource("TextSecondaryBrush");
                button.ToolTip = $"添加常用颜色 {index + 1}";
                button.Click += ChooseTextColor_Click;
            }
            FavoriteColorPanel.Children.Add(button);
        }
    }

    private void FavoriteTextColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: System.Windows.Media.Color color })
        {
            ApplyInk(color);
            TextColorPopup.IsOpen = false;
        }
    }

    private void TimeSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressSelectionAnimation || TimePickerSurface is null)
        {
            return;
        }

        AnimateTimeSelection();
    }

    private void AnimateTimeSelection()
    {
        var from = System.Windows.Media.Color.FromArgb(54, 20, 125, 118);
        var to = System.Windows.Media.Color.FromArgb(0, 20, 125, 118);
        var brush = new SolidColorBrush(from);
        TimePickerSurface.Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ReminderLevel_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressSelectionAnimation || sender is not RadioButton button)
        {
            return;
        }

        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.96, 0.96);
        button.RenderTransform = scale;
        var easing = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut };
        var animation = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        button.BeginAnimation(OpacityProperty, new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(160)));
    }

    internal void OpenTextColorPaletteForVisualTest()
    {
        RefreshFavoriteTextColors();
        TextColorPopup.IsOpen = true;
    }

    internal void CloseTextColorPaletteForVisualTest() => TextColorPopup.IsOpen = false;

    internal FrameworkElement TextColorPaletteVisualForTest => (FrameworkElement)TextColorPopup.Child;

    internal void ConfigurePreciseReminderForVisualTest()
    {
        ReminderPanel.Visibility = Visibility.Visible;
        _suppressSelectionAnimation = true;
        try
        {
            ReminderHour.SelectedItem = "14";
            ReminderMinute.SelectedItem = "37";
            ReminderSecond.SelectedItem = "42";
            LevelStrong.IsChecked = true;
        }
        finally
        {
            _suppressSelectionAnimation = false;
        }
    }

    internal ToolTip OpenReminderTooltipForVisualTest()
    {
        var tooltip = new ToolTip
        {
            Content = LevelUltra.ToolTip,
            PlacementTarget = LevelUltra,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Top,
            IsOpen = true
        };
        return tooltip;
    }

    private void ApplyInk(System.Windows.Media.Color color)
    {
        Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
        Editor.Focus();
    }

    private static string LevelLabel(ReminderLevel level) => level switch
    {
        ReminderLevel.Weak => "弱提醒",
        ReminderLevel.Normal => "普通提醒",
        ReminderLevel.Strong => "强提醒",
        ReminderLevel.Ultra => "超强提醒",
        _ => "提醒"
    };

    private static DateTime TrimMilliseconds(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);

    private static bool TryParseTimePart(string text, int maximum, out int value) =>
        int.TryParse(text.Trim(), out value) && value >= 0 && value <= maximum;

    private sealed class Win32DialogOwner(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }

}
