using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using PinNote.Core.Models;
using PinNote.Core.Updates;

namespace PinNote.Services;

internal sealed record PreparedUpdate(string PackagePath, string ManifestPath, string LauncherPath);

internal sealed class UpdateClient
{
    private const string ManifestBaseUrl =
        "https://github.com/Kratosmax/PinNote/releases/latest/download/";
    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };
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

    public async Task<UpdateInfo?> CheckAsync(
        UpdateNetworkSettings? networkSettings = null,
        CancellationToken cancellationToken = default)
    {
        var channel = CurrentChannel;
        var manifestUri = new Uri(ManifestBaseUrl + UpdateTrust.GetManifestFileName(channel));
        var settings = (networkSettings ?? UpdateNetworkSettings.Default).Normalize();
        using var client = CreateClient(settings);
        Exception? lastError = null;
        string? lastRoute = null;
        foreach (var route in UpdateRouteBuilder.Build(manifestUri, settings))
        {
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                using var response = await client.GetAsync(route.RequestUri, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                EnsureAllowedResponse(response, route);
                if (response.Content.Headers.ContentLength is > UpdateManifestCodec.MaximumManifestSize)
                {
                    throw new InvalidDataException("更新清单超过允许大小。");
                }

                await using var source = await response.Content.ReadAsStreamAsync(requestTimeout.Token).ConfigureAwait(false);
                using var memory = new MemoryStream();
                await BoundedStream.CopyToAsync(source, memory, UpdateManifestCodec.MaximumManifestSize, requestTimeout.Token)
                    .ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(memory.ToArray());
                var update = UpdateManifestCodec.ParseAndVerify(json, UpdateTrust.PublicKeyPem, channel);
                return update.Version > CurrentVersion ? update : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                               or IOException or InvalidDataException
                                               or System.Security.Cryptography.CryptographicException)
            {
                lastError = exception;
                lastRoute = route.DisplayName;
            }
        }

        throw CreateRoutesFailedException(lastRoute, lastError);
    }

    public async Task<PreparedUpdate> DownloadAsync(
        UpdateInfo update,
        IProgress<int> progress,
        UpdateNetworkSettings? networkSettings = null,
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
        var settings = (networkSettings ?? UpdateNetworkSettings.Default).Normalize();
        using var client = CreateClient(settings);
        Exception? lastError = null;
        string? lastRoute = null;
        var downloaded = false;
        foreach (var route in UpdateRouteBuilder.Build(update.DownloadUri, settings))
        {
            try
            {
                TryDelete(temporaryPath);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await client.GetAsync(route.RequestUri, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                EnsureAllowedResponse(response, route);
                if (response.Content.Headers.ContentLength is { } contentLength && contentLength != update.Size)
                {
                    throw new InvalidDataException("服务器返回的更新包大小与签名清单不一致。");
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await UpdatePackageStager.StageAsync(
                    source,
                    temporaryPath,
                    packagePath,
                    update,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                downloaded = true;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(temporaryPath);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                               or IOException or InvalidDataException
                                               or System.Security.Cryptography.CryptographicException)
            {
                lastError = exception;
                lastRoute = route.DisplayName;
                TryDelete(temporaryPath);
            }
        }

        if (!downloaded)
        {
            throw CreateRoutesFailedException(lastRoute, lastError);
        }
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

    private static HttpClient CreateClient(UpdateNetworkSettings settings)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        if (settings.HttpProxy is not null)
        {
            handler.Proxy = new WebProxy(settings.HttpProxy);
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PinNote", CurrentVersion.ToString(3)));
        return client;
    }

    private static void EnsureAllowedResponse(HttpResponseMessage response, UpdateRequestRoute route)
    {
        var responseUri = response.RequestMessage?.RequestUri;
        var allowed = responseUri is not null && (route.IsDirect
            ? responseUri.Scheme == Uri.UriSchemeHttps && AllowedRedirectHosts.Contains(responseUri.Host)
            : (responseUri.Scheme == Uri.UriSchemeHttp || responseUri.Scheme == Uri.UriSchemeHttps)
              && string.Equals(responseUri.Host, route.RequestUri.Host, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new InvalidDataException("更新请求被重定向到不受信任的地址。");
        }
    }

    private static HttpRequestException CreateRoutesFailedException(string? route, Exception? error)
    {
        var routeText = string.IsNullOrWhiteSpace(route) ? "无可用线路" : route;
        var detail = error is null ? "请检查网络设置。" : error.Message;
        return new HttpRequestException($"所有更新线路均失败。最后线路：{routeText}。{detail}", error,
            (error as HttpRequestException)?.StatusCode);
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
