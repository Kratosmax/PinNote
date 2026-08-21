namespace PinNote.Core.Models;

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupId { get; set; }

    public Guid? ParentId { get; set; }

    public string Title { get; set; } = "新待办";

    public int SortOrder { get; set; }

    public bool IsCompleted { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? ReminderAt { get; set; }

    public ReminderLevel ReminderLevel { get; set; } = ReminderLevel.Normal;

    public ReminderState ReminderState { get; set; } = ReminderState.Scheduled;

    public DateTimeOffset? LastTriggeredAt { get; set; }

    public bool IsOverdue(DateTimeOffset now) =>
        !IsCompleted && ReminderAt is { } due && due <= now;

    public TodoItem Clone() => new()
    {
        Id = Id,
        GroupId = GroupId,
        ParentId = ParentId,
        Title = Title,
        SortOrder = SortOrder,
        IsCompleted = IsCompleted,
        CompletedAt = CompletedAt,
        ReminderAt = ReminderAt,
        ReminderLevel = ReminderLevel,
        ReminderState = ReminderState,
        LastTriggeredAt = LastTriggeredAt
    };

    public void Normalize()
    {
        Title = string.IsNullOrWhiteSpace(Title) ? "未命名待办" : Title.Trim();
        CompletedAt = IsCompleted ? CompletedAt ?? DateTimeOffset.Now : null;
    }
}
