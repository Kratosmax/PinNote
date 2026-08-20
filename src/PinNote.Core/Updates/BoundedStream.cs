namespace PinNote.Core.Updates;

public static class BoundedStream
{
    public static async Task<long> CopyToAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }
            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException("下载内容超过允许大小。");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
