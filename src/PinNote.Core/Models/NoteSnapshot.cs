namespace PinNote.Core.Models;

public sealed class NoteSnapshot
{
    public int SchemaVersion { get; set; } = 2;

    public List<NoteDocument> Notes { get; set; } = [];

    public List<NoteGroup> Groups { get; set; } = [];

    public AppSettings Settings { get; set; } = new();

    public NoteSnapshot Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        Notes = Notes.Select(note => note.Clone()).ToList(),
        Groups = Groups.Select(group => group.Clone()).ToList(),
        Settings = Settings.Clone()
    };

    public void Normalize()
    {
        Notes ??= [];
        Groups ??= [];
        Settings ??= new AppSettings();
        Settings.Normalize();
        SchemaVersion = 2;
        Notes.RemoveAll(note => note is null);
        Groups.RemoveAll(group => group is null);
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
    }
}
