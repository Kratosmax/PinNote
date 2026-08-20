namespace PinNote.Core.Updates;

public interface IUpdateProvider
{
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed record UpdateInfo(Version Version, Uri ManifestUri, string Sha256, string Signature);

public sealed class NullUpdateProvider : IUpdateProvider
{
    public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<UpdateInfo?>(null);
}
