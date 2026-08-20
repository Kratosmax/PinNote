using System.Text.Json;
using System.Text.Json.Serialization;
using PinNote.Core.Models;

namespace PinNote.Core.Storage;

public sealed class JsonNoteStore(string filePath) : INoteStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; } = Path.GetFullPath(filePath);

    public async Task<NoteSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return new NoteSnapshot();
        }

        try
        {
            return await LoadFileAsync(FilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) when (File.Exists(FilePath + ".bak"))
        {
            return await LoadFileAsync(FilePath + ".bak", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<NoteSnapshot> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var snapshot = await JsonSerializer.DeserializeAsync<NoteSnapshot>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false) ?? new NoteSnapshot();
        snapshot.Normalize();
        return snapshot;
    }

    public async Task SaveAsync(NoteSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Normalize();

        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The note store path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(FilePath))
            {
                File.Replace(temporaryPath, FilePath, FilePath + ".bak", true);
            }
            else
            {
                File.Move(temporaryPath, FilePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
