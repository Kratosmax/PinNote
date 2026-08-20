using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using PinNote.Core.Updates;

namespace PinNote.Services;

internal sealed record PreparedUpdate(string PackagePath, string ManifestPath, string LauncherPath);

internal sealed class UpdateClient : IDisposable
{
    private const string ManifestBaseUrl =
        "https://github.com/Kratosmax/PinNote/releases/latest/download/";
    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };
    private readonly HttpClient _httpClient;

    public UpdateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PinNote", CurrentVersion.ToString(3)));
    }

    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    public bool CanInstallInPlace
    {
        get
        {
            try
            {
                _ = UpdateInstaller.GetInstalledChannel(AppContext.BaseDirectory);
                return File.Exists(Path.Combine(AppContext.BaseDirectory, "PinNote.Updater.exe"));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static string CurrentChannel
    {
        get
        {
            try
            {
                return UpdateInstaller.GetInstalledChannel(AppContext.BaseDirectory);
            }
            catch (InvalidOperationException)
            {
                return UpdateTrust.LiteChannel;
            }
        }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var channel = CurrentChannel;
        var manifestUri = new Uri(ManifestBaseUrl + UpdateTrust.GetManifestFileName(channel));
        using var response = await _httpClient.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnsureAllowedResponse(response);
        if (response.Content.Headers.ContentLength is > UpdateManifestCodec.MaximumManifestSize)
        {
            throw new InvalidDataException("更新清单超过允许大小。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        await BoundedStream.CopyToAsync(source, memory, UpdateManifestCodec.MaximumManifestSize, timeout.Token)
            .ConfigureAwait(false);
        var json = Encoding.UTF8.GetString(memory.ToArray());
        var update = UpdateManifestCodec.ParseAndVerify(json, UpdateTrust.PublicKeyPem, channel);
        return update.Version > CurrentVersion ? update : null;
    }

    public async Task<PreparedUpdate> DownloadAsync(
        UpdateInfo update,
        IProgress<int> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(progress);
        if (!CanInstallInPlace)
        {
            throw new InvalidOperationException("当前运行目录不是 PinNote 正式便携包，不能执行就地更新。");
        }

        var updateRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PinNote",
            "updates",
            update.Version.ToString(3));
        Directory.CreateDirectory(updateRoot);
        CleanupOldUpdates(Path.GetDirectoryName(updateRoot)!);

        var packagePath = Path.Combine(updateRoot, "package.zip");
        var temporaryPath = packagePath + ".download";
        TryDelete(temporaryPath);
        try
        {
            using var response = await _httpClient.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            EnsureAllowedResponse(response);
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != update.Size)
            {
                throw new InvalidDataException("服务器返回的更新包大小与签名清单不一致。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyDownloadAsync(source, destination, update.Size, progress, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, packagePath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }

        await UpdatePackageValidator.ValidateAsync(packagePath, update, cancellationToken).ConfigureAwait(false);
        var manifestPath = Path.Combine(updateRoot, "update.json");
        await File.WriteAllTextAsync(manifestPath, update.RawManifest, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        var launcherDirectory = Path.Combine(updateRoot, "launcher");
        Directory.CreateDirectory(launcherDirectory);
        var launcherPath = Path.Combine(launcherDirectory, "PinNote.Updater.exe");
        var launcherFiles = update.Channel == UpdateTrust.FullChannel
            ? new[] { "PinNote.Updater.exe" }
            : new[]
            {
                "PinNote.Updater.exe",
                "PinNote.Updater.dll",
                "PinNote.Updater.deps.json",
                "PinNote.Updater.runtimeconfig.json",
                "PinNote.Core.dll"
            };
        foreach (var fileName in launcherFiles)
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, fileName), Path.Combine(launcherDirectory, fileName), overwrite: true);
        }
        progress.Report(100);
        return new PreparedUpdate(packagePath, manifestPath, launcherPath);
    }

    public static void LaunchUpdater(PreparedUpdate prepared)
    {
        var startInfo = new ProcessStartInfo(prepared.LauncherPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(prepared.LauncherPath)!
        };
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add(prepared.PackagePath);
        startInfo.ArgumentList.Add("--manifest");
        startInfo.ArgumentList.Add(prepared.ManifestPath);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(AppContext.BaseDirectory);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 PinNote 更新器。");
    }

    public void Dispose() => _httpClient.Dispose();

    private static async Task CopyDownloadAsync(
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

    private static void EnsureAllowedResponse(HttpResponseMessage response)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        if (responseUri is null || responseUri.Scheme != Uri.UriSchemeHttps || !AllowedRedirectHosts.Contains(responseUri.Host))
        {
            throw new InvalidDataException("更新请求被重定向到不受信任的地址。");
        }
    }

    private static void CleanupOldUpdates(string updatesRoot)
    {
        if (!Directory.Exists(updatesRoot))
        {
            return;
        }
        foreach (var directory in Directory.EnumerateDirectories(updatesRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-14))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
