namespace PinNote.Core.Updates;

public static class UpdatePackageStager
{
    public static async Task StageAsync(
        Stream source,
        string temporaryPath,
        string packagePath,
        UpdateInfo update,
        IProgress<int> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyAsync(source, destination, update.Size, progress, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await UpdatePackageValidator.ValidateAsync(temporaryPath, update, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, packagePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task CopyAsync(
        Stream source,
        Stream destination,
        long expectedSize,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idleTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var read = await source.ReadAsync(buffer, idleTimeout.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            total += read;
            if (total > expectedSize || total > UpdateManifestCodec.MaximumPackageSize)
            {
                throw new InvalidDataException("下载内容超过签名清单声明的大小。");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            progress.Report((int)Math.Min(99, total * 100 / expectedSize));
        }
        if (total != expectedSize)
        {
            throw new EndOfStreamException("更新包下载不完整。");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
