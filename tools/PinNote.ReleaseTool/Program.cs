using System.Security.Cryptography;
using System.Text.Json;
using PinNote.Core.Updates;

try
{
    var options = ParseOptions(args.Skip(1).ToArray());
    switch (args.FirstOrDefault())
    {
        case "metadata":
            await WriteMetadataAsync(options);
            break;
        case "manifest":
            await WriteManifestAsync(options);
            break;
        case "verify":
            await VerifyAsync(options);
            break;
        default:
            throw new ArgumentException("用法：PinNote.ReleaseTool metadata|manifest|verify [参数]");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static async Task WriteMetadataAsync(IReadOnlyDictionary<string, string> options)
{
    var version = RequireVersion(options);
    var channel = RequireChannel(options);
    var output = Require(options, "output");
    var metadata = new PinNotePackageMetadata
    {
        Version = version.ToString(3),
        Channel = channel
    };
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
}

static async Task WriteManifestAsync(IReadOnlyDictionary<string, string> options)
{
    var version = RequireVersion(options);
    var channel = RequireChannel(options);
    var packagePath = Path.GetFullPath(Require(options, "package"));
    var privateKeyPath = Path.GetFullPath(Require(options, "private-key"));
    var output = Path.GetFullPath(Require(options, "output"));
    var downloadUrl = Require(options, "download-url");
    var releaseNotes = options.TryGetValue("release-notes", out var notesPath)
        ? await File.ReadAllTextAsync(notesPath)
        : string.Empty;

    var file = new FileInfo(packagePath);
    await using var packageStream = file.OpenRead();
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream));
    var unsigned = new UpdateManifest
    {
        Version = version.ToString(3),
        Channel = channel,
        DownloadUrl = downloadUrl,
        Size = file.Length,
        Sha256 = hash,
        ReleaseNotes = releaseNotes
    };

    var provisional = new UpdateInfo(version, channel, new Uri(downloadUrl), file.Length, hash, releaseNotes, string.Empty);
    await UpdatePackageValidator.ValidateAsync(packagePath, provisional);
    var signature = UpdateManifestCodec.Sign(unsigned, await File.ReadAllTextAsync(privateKeyPath));
    var signed = new UpdateManifest
    {
        SchemaVersion = unsigned.SchemaVersion,
        Version = unsigned.Version,
        Channel = unsigned.Channel,
        DownloadUrl = unsigned.DownloadUrl,
        Size = unsigned.Size,
        Sha256 = unsigned.Sha256,
        Signature = signature,
        ReleaseNotes = unsigned.ReleaseNotes
    };
    await File.WriteAllTextAsync(output, UpdateManifestCodec.Serialize(signed));
}

static async Task VerifyAsync(IReadOnlyDictionary<string, string> options)
{
    var manifestPath = Path.GetFullPath(Require(options, "manifest"));
    var packagePath = Path.GetFullPath(Require(options, "package"));
    var channel = RequireChannel(options);
    var json = await File.ReadAllTextAsync(manifestPath);
    var update = UpdateManifestCodec.ParseAndVerify(json, UpdateTrust.PublicKeyPem, channel);
    await UpdatePackageValidator.ValidateAsync(packagePath, update);
    Console.WriteLine($"VERIFIED {update.Version.ToString(3)} {update.Sha256}");
}

static Version RequireVersion(IReadOnlyDictionary<string, string> options)
{
    var value = Require(options, "version");
    if (!Version.TryParse(value, out var version) || version.Build < 0 || version.Revision >= 0)
    {
        throw new ArgumentException("version 必须是三段数字版本。");
    }
    return version;
}

static string RequireChannel(IReadOnlyDictionary<string, string> options)
{
    var channel = Require(options, "channel");
    if (!UpdateTrust.IsSupportedChannel(channel))
    {
        throw new ArgumentException("channel 必须是受支持的 Lite 或 Full 通道。");
    }
    return channel;
}

static Dictionary<string, string> ParseOptions(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("参数格式无效。");
        }
        result.Add(values[index][2..], values[index + 1]);
    }
    return result;
}

static string Require(IReadOnlyDictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"缺少参数 --{name}。");
