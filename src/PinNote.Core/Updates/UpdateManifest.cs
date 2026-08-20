using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PinNote.Core.Updates;

public sealed class UpdateManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; init; } = string.Empty;

    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; init; } = string.Empty;
}

public sealed record UpdateInfo(
    Version Version,
    string Channel,
    Uri DownloadUri,
    long Size,
    string Sha256,
    string ReleaseNotes,
    string RawManifest);

public static class UpdateManifestCodec
{
    public const long MaximumPackageSize = 200L * 1024 * 1024;
    public const int MaximumManifestSize = 64 * 1024;
    private const string ReleasePathPrefix = "/Kratosmax/PinNote/releases/download/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static UpdateInfo ParseAndVerify(string json, string publicKeyPem, string expectedChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedChannel);

        if (Encoding.UTF8.GetByteCount(json) > MaximumManifestSize)
        {
            throw new InvalidDataException("更新清单超过允许大小。");
        }

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("更新清单为空。");
        Validate(manifest, expectedChannel);

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("更新清单签名格式无效。", exception);
        }

        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        if (!rsa.VerifyData(
                Encoding.UTF8.GetBytes(GetCanonicalPayload(manifest)),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
        {
            throw new CryptographicException("更新清单签名验证失败。");
        }

        return new UpdateInfo(
            System.Version.Parse(manifest.Version),
            manifest.Channel,
            new Uri(manifest.DownloadUrl),
            manifest.Size,
            manifest.Sha256.ToUpperInvariant(),
            manifest.ReleaseNotes,
            json);
    }

    public static string Sign(UpdateManifest manifest, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        Validate(manifest, manifest.Channel, requireSignature: false);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(rsa.SignData(
            Encoding.UTF8.GetBytes(GetCanonicalPayload(manifest)),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    public static string Serialize(UpdateManifest manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

    private static void Validate(UpdateManifest manifest, string expectedChannel, bool requireSignature = true)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidDataException("不支持的更新清单版本。");
        }
        if (!System.Version.TryParse(manifest.Version, out var version) ||
            version.Major < 0 || version.Build < 0 || version.Revision >= 0)
        {
            throw new InvalidDataException("更新版本必须是三段 SemVer 数字版本。");
        }
        if (!string.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新通道不匹配。");
        }
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(ReleasePathPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新下载地址不在允许的 GitHub Release 范围内。");
        }
        if (manifest.Size <= 0 || manifest.Size > MaximumPackageSize)
        {
            throw new InvalidDataException("更新包大小超出允许范围。");
        }
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("更新包 SHA-256 格式无效。");
        }
        if (requireSignature && string.IsNullOrWhiteSpace(manifest.Signature))
        {
            throw new InvalidDataException("更新清单缺少签名。");
        }
        if (Encoding.UTF8.GetByteCount(manifest.ReleaseNotes) > 16 * 1024)
        {
            throw new InvalidDataException("更新说明超过允许大小。");
        }
    }

    private static string GetCanonicalPayload(UpdateManifest manifest) => string.Join('\n',
        manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
        manifest.Version,
        manifest.Channel,
        manifest.DownloadUrl,
        manifest.Size.ToString(CultureInfo.InvariantCulture),
        manifest.Sha256.ToUpperInvariant());
}
