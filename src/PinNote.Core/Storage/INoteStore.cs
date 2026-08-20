using PinNote.Core.Models;

namespace PinNote.Core.Storage;

public interface INoteStore
{
    Task<NoteSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(NoteSnapshot snapshot, CancellationToken cancellationToken = default);
}
