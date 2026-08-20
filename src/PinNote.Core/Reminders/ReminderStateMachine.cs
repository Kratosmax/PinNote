using PinNote.Core.Models;

namespace PinNote.Core.Reminders;

public static class ReminderStateMachine
{
    public static void Schedule(NoteDocument note, DateTimeOffset due, ReminderLevel level)
    {
        note.ReminderAt = due;
        note.ReminderLevel = level;
        note.ReminderState = ReminderState.Scheduled;
        note.LastTriggeredAt = null;
    }

    public static void Trigger(NoteDocument note, DateTimeOffset now)
    {
        if (note.ReminderAt is null || note.ReminderState != ReminderState.Scheduled)
        {
            return;
        }

        note.LastTriggeredAt = now;
        note.ReminderState = note.ReminderLevel is ReminderLevel.Weak or ReminderLevel.Normal
            ? ReminderState.Dismissed
            : ReminderState.Triggered;
    }

    public static void Snooze(NoteDocument note, DateTimeOffset due)
    {
        note.ReminderAt = due;
        note.ReminderState = ReminderState.Scheduled;
        note.LastTriggeredAt = null;
    }

    public static void Dismiss(NoteDocument note)
    {
        if (note.ReminderAt is not null)
        {
            note.ReminderState = ReminderState.Dismissed;
        }
    }

    public static void Complete(NoteDocument note)
    {
        note.ReminderAt = null;
        note.ReminderState = ReminderState.Scheduled;
        note.LastTriggeredAt = null;
    }
}
