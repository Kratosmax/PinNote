using PinNote.Core.Reminders;

namespace PinNote.Core.Models;

public static class ItemLifecycle
{
    public static void MoveToTrash(NoteDocument note, DateTimeOffset now)
    {
        note.DeletedAt = now;
        note.IsHidden = true;
        note.ReminderState = ReminderState.Dismissed;
    }

    public static IReadOnlySet<Guid> MoveTodoTreeToTrash(IEnumerable<TodoItem> items, TodoItem root, DateTimeOffset now)
    {
        var ids = TodoPlanner.Descendants(items, root.Id).Select(item => item.Id).Append(root.Id).ToHashSet();
        foreach (var item in items.Where(item => ids.Contains(item.Id)))
        {
            item.DeletedAt = now;
            item.ReminderState = ReminderState.Dismissed;
        }
        return ids;
    }

    public static int PurgeExpired(NoteSnapshot snapshot, DateTimeOffset now, int retentionDays)
    {
        var cutoff = now.AddDays(-Math.Clamp(retentionDays, 1, 3650));
        var notes = snapshot.Notes.RemoveAll(note => note.DeletedAt is { } deleted && deleted <= cutoff);
        var todos = snapshot.TodoItems.RemoveAll(item => item.DeletedAt is { } deleted && deleted <= cutoff);
        return notes + todos;
    }

    public static void Restore(NoteDocument note)
    {
        note.DeletedAt = null;
        note.IsHidden = true;
        if (note.ReminderAt is not null)
        {
            note.ReminderState = ReminderState.Scheduled;
        }
    }

    public static IReadOnlyList<TodoItem> RestoreTodoTree(NoteSnapshot snapshot, TodoItem root)
    {
        var restored = TodoPlanner.Descendants(snapshot.TodoItems, root.Id, includeDeleted: true)
            .Append(root).DistinctBy(item => item.Id).ToArray();
        if (snapshot.TodoGroups.All(group => group.Id != root.GroupId))
        {
            var group = new TodoGroup { Name = "已恢复待办", SortOrder = snapshot.TodoGroups.Count };
            snapshot.TodoGroups.Add(group);
            foreach (var item in restored) item.GroupId = group.Id;
        }
        var restoredIds = restored.Select(item => item.Id).ToHashSet();
        foreach (var item in restored)
        {
            item.DeletedAt = null;
            if (item.ParentId is { } parentId && !restoredIds.Contains(parentId) &&
                snapshot.TodoItems.FirstOrDefault(parent => parent.Id == parentId)?.DeletedAt is not null)
            {
                item.ParentId = null;
            }
            if (item.ReminderAt is not null) item.ReminderState = ReminderState.Scheduled;
        }
        return restored;
    }

    public static NoteDocument Duplicate(NoteDocument source, DateTimeOffset now)
    {
        var copy = source.Clone();
        copy.Id = Guid.NewGuid();
        copy.Title = $"{source.Title} 副本";
        copy.Left += 24;
        copy.Top += 24;
        copy.IsHidden = false;
        copy.ModifiedAt = now;
        copy.DeletedAt = null;
        copy.LastTriggeredAt = null;
        if (copy.ReminderAt is not null) copy.ReminderState = ReminderState.Scheduled;
        return copy;
    }

    public static IReadOnlyList<TodoItem> DuplicateTodoTree(IList<TodoItem> items, TodoItem source)
    {
        var originals = TodoPlanner.Descendants(items, source.Id).Prepend(source).ToArray();
        foreach (var sibling in items.Where(item => item.DeletedAt is null && item.Id != source.Id &&
                     item.GroupId == source.GroupId && item.ParentId == source.ParentId && item.SortOrder > source.SortOrder))
            sibling.SortOrder++;
        var newIds = originals.ToDictionary(item => item.Id, _ => Guid.NewGuid());
        var copies = new List<TodoItem>(originals.Length);
        foreach (var original in originals)
        {
            var copy = original.Clone();
            copy.Id = newIds[original.Id];
            copy.ParentId = original.Id == source.Id ? source.ParentId :
                original.ParentId is { } parentId && newIds.TryGetValue(parentId, out var mapped) ? mapped : null;
            copy.Title = original.Id == source.Id ? $"{original.Title} 副本" : original.Title;
            if (original.Id == source.Id) copy.SortOrder = source.SortOrder + 1;
            copy.IsCompleted = false;
            copy.CompletedAt = null;
            copy.DeletedAt = null;
            copy.LastTriggeredAt = null;
            if (copy.ReminderAt is not null) copy.ReminderState = ReminderState.Scheduled;
            copies.Add(copy);
        }
        foreach (var copy in copies) items.Add(copy);
        return copies;
    }
}
