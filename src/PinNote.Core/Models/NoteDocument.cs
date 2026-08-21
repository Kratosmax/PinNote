namespace PinNote.Core.Models;

public sealed class NoteDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = "新便签";

    public string RtfContent { get; set; } = string.Empty;

    public double Left { get; set; } = 120;

    public double Top { get; set; } = 120;

    public double Width { get; set; } = 360;

    public double Height { get; set; } = 420;

    public PinMode PinMode { get; set; } = PinMode.Desktop;

    public Guid? GroupId { get; set; }

    public bool IsHidden { get; set; }

    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? ReminderAt { get; set; }

    public ReminderLevel ReminderLevel { get; set; } = ReminderLevel.Normal;

    public ReminderState ReminderState { get; set; } = ReminderState.Scheduled;

    public DateTimeOffset? LastTriggeredAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsOverdue(DateTimeOffset now) => ReminderAt is { } due && due <= now;

    public NoteDocument Clone() => new()
    {
        Id = Id,
        Title = Title,
        RtfContent = RtfContent,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        PinMode = PinMode,
        GroupId = GroupId,
        IsHidden = IsHidden,
        ModifiedAt = ModifiedAt,
        ReminderAt = ReminderAt,
        ReminderLevel = ReminderLevel,
        ReminderState = ReminderState,
        LastTriggeredAt = LastTriggeredAt,
        DeletedAt = DeletedAt
    };

    public void Normalize()
    {
        Title = string.IsNullOrWhiteSpace(Title) ? "新便签" : Title.Trim();
        Width = Math.Clamp(Width, 280, 1600);
        Height = Math.Clamp(Height, 260, 1200);
        Left = double.IsFinite(Left) ? Left : 120;
        Top = double.IsFinite(Top) ? Top : 120;
        ModifiedAt = ModifiedAt == default ? DateTimeOffset.Now : ModifiedAt;
    }
}
