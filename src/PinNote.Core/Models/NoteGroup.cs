namespace PinNote.Core.Models;

public sealed class NoteGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新分组";

    public int SortOrder { get; set; }

    public NoteGroup Clone() => new()
    {
        Id = Id,
        Name = Name,
        SortOrder = SortOrder
    };

    public void Normalize() => Name = string.IsNullOrWhiteSpace(Name) ? "未命名分组" : Name.Trim();
}
