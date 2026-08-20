using System.IO.Compression;
using System.Text.Json;

namespace PinNote.Core.Updates;

public static class UpdateInstaller
{
    private const string TransactionDirectoryName = ".pinnote-update";

    public static async Task InstallAsync(
        string packagePath,
        string targetDirectory,
        UpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        var targetRoot = EnsureInstallRoot(targetDirectory, update.Channel);
        await UpdatePackageValidator.ValidateAsync(packagePath, update, cancellationToken).ConfigureAwait(false);

        var transactionRoot = Path.Combine(targetRoot, TransactionDirectoryName, Guid.NewGuid().ToString("N"));
        var stageRoot = Path.Combine(transactionRoot, "stage");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        Directory.CreateDirectory(stageRoot);
        Directory.CreateDirectory(backupRoot);

        var installed = new List<string>();
        var backups = new List<(string Target, string Backup)>();
        try
        {
            await ExtractAsync(packagePath, stageRoot, cancellationToken).ConfigureAwait(false);
            foreach (var sourcePath in Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(stageRoot, sourcePath);
                var targetPath = ResolveInside(targetRoot, relativePath);
                var backupPath = ResolveInside(backupRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                if (File.Exists(targetPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Move(targetPath, backupPath);
                    backups.Add((targetPath, backupPath));
                }
                File.Move(sourcePath, targetPath);
                installed.Add(targetPath);
            }
        }
        catch
        {
            foreach (var installedPath in installed.AsEnumerable().Reverse())
            {
                TryDelete(installedPath);
            }
            foreach (var (target, backup) in backups.AsEnumerable().Reverse())
            {
                if (File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(backup, target, overwrite: true);
                }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(transactionRoot);
        }
    }

    public static string EnsureInstallRoot(string targetDirectory, string? expectedChannel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var targetRoot = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var markerPath = Path.Combine(targetRoot, "pinnote-install.json");
        var executablePath = Path.Combine(targetRoot, "PinNote.exe");
        if (!File.Exists(markerPath) || !File.Exists(executablePath))
        {
            throw new InvalidOperationException("目标目录不是可更新的 PinNote 便携安装目录。");
        }

        var marker = JsonSerializer.Deserialize<PinNotePackageMetadata>(File.ReadAllText(markerPath));
        if (marker?.ProductId != UpdateTrust.ProductId ||
            !UpdateTrust.IsSupportedChannel(marker.Channel) ||
            (expectedChannel is not null && marker.Channel != expectedChannel))
        {
            throw new InvalidOperationException("目标目录的产品或更新通道不匹配。");
        }
        return targetRoot;
    }

    public static string GetInstalledChannel(string targetDirectory)
    {
        var targetRoot = EnsureInstallRoot(targetDirectory);
        var marker = JsonSerializer.Deserialize<PinNotePackageMetadata>(
            File.ReadAllText(Path.Combine(targetRoot, "pinnote-install.json")))
            ?? throw new InvalidOperationException("PinNote 安装标记无效。");
        return marker.Channel;
    }

    private static async Task ExtractAsync(string packagePath, string stageRoot, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = UpdatePackageValidator.NormalizeRelativePath(entry.FullName);
            if (relativePath.Length == 0)
            {
                continue;
            }
            var destination = ResolveInside(stageRoot, relativePath);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            await BoundedStream.CopyToAsync(source, target, UpdateManifestCodec.MaximumPackageSize, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string ResolveInside(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"路径超出允许目录：{relativePath}");
        }
        return fullPath;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
