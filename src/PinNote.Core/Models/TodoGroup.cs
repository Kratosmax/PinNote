namespace PinNote.Core.Models;

public sealed class TodoGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "新待办分组";

    public int SortOrder { get; set; }

    public double Left { get; set; } = 180;

    public double Top { get; set; } = 160;

    public double Width { get; set; } = 380;

    public double Height { get; set; } = 460;

    public PinMode PinMode { get; set; } = PinMode.Desktop;

    public bool IsHidden { get; set; }

    public TodoGroup Clone() => new()
    {
        Id = Id,
        Name = Name,
        SortOrder = SortOrder,
        Left = Left,
        Top = Top,
        Width = Width,
        Height = Height,
        PinMode = PinMode,
        IsHidden = IsHidden
    };

    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "未命名待办分组" : Name.Trim();
        Width = Math.Clamp(Width, 300, 1600);
        Height = Math.Clamp(Height, 260, 1200);
        Left = double.IsFinite(Left) ? Left : 180;
        Top = double.IsFinite(Top) ? Top : 160;
    }
}
