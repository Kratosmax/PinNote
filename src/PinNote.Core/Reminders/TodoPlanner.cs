using PinNote.Core.Models;

namespace PinNote.Core.Reminders;

public static class TodoPlanner
{
    public static IReadOnlyList<TodoItem> GetDue(IEnumerable<TodoItem> items, DateTimeOffset now) =>
        items
            .Where(item => item.DeletedAt is null && !item.IsCompleted && item.ReminderAt is { } due && due <= now && item.ReminderState == ReminderState.Scheduled)
            .OrderBy(item => item.ReminderAt)
            .ToArray();

    public static DateTimeOffset? GetNextDue(IEnumerable<TodoItem> items, DateTimeOffset now) =>
        items
            .Where(item => item.DeletedAt is null && !item.IsCompleted && item.ReminderAt is { } due && due > now && item.ReminderState == ReminderState.Scheduled)
            .Select(item => item.ReminderAt!.Value)
            .DefaultIfEmpty()
            .Min() is var next && next != default ? next : null;

    public static void Schedule(TodoItem item, DateTimeOffset due, ReminderLevel level = ReminderLevel.Normal)
    {
        item.ReminderAt = due;
        item.ReminderLevel = level;
        item.ReminderState = ReminderState.Scheduled;
        item.LastTriggeredAt = null;
    }

    public static void ClearReminder(TodoItem item)
    {
        item.ReminderAt = null;
        item.ReminderState = ReminderState.Scheduled;
        item.LastTriggeredAt = null;
    }

    public static void Trigger(TodoItem item, DateTimeOffset now)
    {
        if (item.DeletedAt is not null || item.ReminderAt is null || item.ReminderState != ReminderState.Scheduled || item.IsCompleted)
        {
            return;
        }

        item.LastTriggeredAt = now;
        item.ReminderState = item.ReminderLevel is ReminderLevel.Weak or ReminderLevel.Normal
            ? ReminderState.Dismissed
            : ReminderState.Triggered;
    }

    public static void Snooze(TodoItem item, DateTimeOffset due) =>
        Schedule(item, due, item.ReminderLevel);

    public static void Dismiss(TodoItem item) =>
        item.ReminderState = ReminderState.Dismissed;

    public static void SetCompleted(TodoItem item, bool completed, DateTimeOffset now)
    {
        item.IsCompleted = completed;
        item.CompletedAt = completed ? now : null;
    }

    public static IReadOnlyList<TodoItem> CompleteEligibleAncestors(
        IEnumerable<TodoItem> items,
        TodoItem item,
        DateTimeOffset now,
        Func<TodoItem, bool> shouldComplete)
    {
        var allItems = items.Where(todo => todo.DeletedAt is null).ToArray();
        var completed = new List<TodoItem>();
        var parentId = item.ParentId;
        while (parentId is { } id && allItems.FirstOrDefault(todo => todo.Id == id) is { } parent)
        {
            if (parent.IsCompleted)
            {
                parentId = parent.ParentId;
                continue;
            }

            var children = allItems.Where(todo => todo.ParentId == parent.Id).ToArray();
            if (children.Length == 0 || children.Any(child => !child.IsCompleted) || !shouldComplete(parent))
            {
                break;
            }

            SetCompleted(parent, true, now);
            completed.Add(parent);
            parentId = parent.ParentId;
        }

        return completed;
    }

    public static IReadOnlyList<TodoItem> Descendants(IEnumerable<TodoItem> items, Guid parentId, bool includeDeleted = false)
    {
        var byParent = items.Where(item => item.ParentId is not null && (includeDeleted || item.DeletedAt is null)).GroupBy(item => item.ParentId!.Value).ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<TodoItem>();
        var queue = new Queue<Guid>();
        queue.Enqueue(parentId);
        while (queue.TryDequeue(out var current) && byParent.TryGetValue(current, out var children))
        {
            foreach (var child in children)
            {
                result.Add(child);
                queue.Enqueue(child.Id);
            }
        }
        return result;
    }
    public static bool Move(
        IList<TodoItem> items,
        TodoItem dragged,
        TodoItem target,
        bool makeChild,
        bool insertAfter = false)
    {
        if (dragged.DeletedAt is not null || target.DeletedAt is not null || dragged.Id == target.Id || dragged.GroupId != target.GroupId ||
            Descendants(items, dragged.Id).Any(item => item.Id == target.Id))
        {
            return false;
        }

        var oldParentId = dragged.ParentId;
        var newParentId = makeChild ? target.Id : target.ParentId;
        dragged.ParentId = newParentId;

        var siblings = items
            .Where(item => item.DeletedAt is null && item.GroupId == dragged.GroupId && item.ParentId == newParentId && item.Id != dragged.Id)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Title)
            .ToList();
        var targetIndex = siblings.FindIndex(item => item.Id == target.Id);
        var insertIndex = makeChild
            ? siblings.Count
            : Math.Clamp(targetIndex + (insertAfter ? 1 : 0), 0, siblings.Count);
        siblings.Insert(insertIndex, dragged);
        Renumber(siblings);

        if (oldParentId != newParentId)
        {
            Renumber(items
                .Where(item => item.DeletedAt is null && item.GroupId == dragged.GroupId && item.ParentId == oldParentId && item.Id != dragged.Id)
                .OrderBy(item => item.SortOrder));
        }

        return true;
    }

    private static void Renumber(IEnumerable<TodoItem> siblings)
    {
        var order = 0;
        foreach (var sibling in siblings)
        {
            sibling.SortOrder = order++;
        }
    }
}
