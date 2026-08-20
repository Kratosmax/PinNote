using PinNote.Core.Models;

namespace PinNote.Core.Reminders;

public static class ReminderPlanner
{
    public static IReadOnlyList<NoteDocument> GetDue(IEnumerable<NoteDocument> notes, DateTimeOffset now) =>
        notes
            .Where(note => note.ReminderAt is { } due && due <= now && note.ReminderState == ReminderState.Scheduled)
            .OrderBy(note => note.ReminderAt)
            .ToArray();

    public static DateTimeOffset? GetNextDue(IEnumerable<NoteDocument> notes, DateTimeOffset now) =>
        notes
            .Where(note => note.ReminderAt is { } due && due > now && note.ReminderState == ReminderState.Scheduled)
            .Select(note => note.ReminderAt!.Value)
            .DefaultIfEmpty()
            .Min() is var next && next != default ? next : null;
}
