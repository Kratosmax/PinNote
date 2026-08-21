namespace PinNote.Core.Models;

public sealed class NoteSnapshot
{
    public int SchemaVersion { get; set; } = 4;

    public List<NoteDocument> Notes { get; set; } = [];

    public List<NoteGroup> Groups { get; set; } = [];

    public List<TodoGroup> TodoGroups { get; set; } = [];

    public List<TodoItem> TodoItems { get; set; } = [];

    public AppSettings Settings { get; set; } = new();

    public NoteSnapshot Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Notes = Notes.Select(note => note.Clone()).ToList(),
        Groups = Groups.Select(group => group.Clone()).ToList(),
        TodoGroups = TodoGroups.Select(group => group.Clone()).ToList(),
        TodoItems = TodoItems.Select(item => item.Clone()).ToList(),
        Settings = Settings.Clone()
    };

    public void Normalize()
    {
        Notes ??= [];
        Groups ??= [];
        TodoGroups ??= [];
        TodoItems ??= [];
        Settings ??= new AppSettings();
        Settings.Normalize();
        SchemaVersion = 4;
        Notes.RemoveAll(note => note is null);
        Groups.RemoveAll(group => group is null);
        TodoGroups.RemoveAll(group => group is null);
        TodoItems.RemoveAll(item => item is null);
        foreach (var group in Groups)
        {
            group.Normalize();
        }
        var groupIds = Groups.Select(group => group.Id).ToHashSet();
        foreach (var note in Notes)
        {
            note.Normalize();
            if (note.GroupId is { } groupId && !groupIds.Contains(groupId))
            {
                note.GroupId = null;
            }
        }

        foreach (var group in TodoGroups)
        {
            group.Normalize();
        }
        var todoGroupIds = TodoGroups.Select(group => group.Id).ToHashSet();
        TodoItems.RemoveAll(item => !todoGroupIds.Contains(item.GroupId));
        foreach (var item in TodoItems)
        {
            item.Normalize();
        }

        var seenTodoIds = new HashSet<Guid>();
        foreach (var item in TodoItems)
        {
            if (seenTodoIds.Add(item.Id))
            {
                continue;
            }
            item.Id = Guid.NewGuid();
            item.ParentId = null;
            seenTodoIds.Add(item.Id);
        }
        var itemById = TodoItems.ToDictionary(item => item.Id);
        foreach (var item in TodoItems)
        {
            if (item.ParentId is not { } parentId || !itemById.TryGetValue(parentId, out var parent) ||
                parent.GroupId != item.GroupId || CreatesCycle(item, itemById))
            {
                item.ParentId = null;
            }
        }
    }

    private static bool CreatesCycle(TodoItem item, IReadOnlyDictionary<Guid, TodoItem> itemById)
    {
        var seen = new HashSet<Guid> { item.Id };
        var parentId = item.ParentId;
        while (parentId is { } current && itemById.TryGetValue(current, out var parent))
        {
            if (!seen.Add(current))
            {
                return true;
            }
            parentId = parent.ParentId;
        }
        return false;
    }
}
