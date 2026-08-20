namespace PinNote.Core.Models;

public sealed record GithubProxySetting(string BaseUrl, int Priority, bool IsDirect = false);

public sealed record UpdateNetworkSettings(
    List<GithubProxySetting>? GithubProxies = null,
    string? HttpProxy = null)
{
    public static UpdateNetworkSettings Default => new([new GithubProxySetting(string.Empty, 10, true)]);

    public UpdateNetworkSettings Normalize()
    {
        var proxies = new List<GithubProxySetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasDirect = false;
        foreach (var proxy in GithubProxies ?? [])
        {
            if (proxy.IsDirect)
            {
                if (!hasDirect)
                {
                    proxies.Add(new GithubProxySetting(string.Empty, Math.Clamp(proxy.Priority, 0, 10), true));
                    hasDirect = true;
                }
                continue;
            }

            if (TryNormalizeGithubProxy(proxy.BaseUrl, out var baseUrl) && seen.Add(baseUrl))
            {
                proxies.Add(new GithubProxySetting(baseUrl, Math.Clamp(proxy.Priority, 0, 10)));
            }
        }

        if (!hasDirect)
        {
            proxies.Insert(0, new GithubProxySetting(string.Empty, proxies.Count == 0 ? 10 : 1, true));
        }

        return new UpdateNetworkSettings(
            proxies,
            TryNormalizeHttpProxy(HttpProxy, out var httpProxy) ? httpProxy : null);
    }

    public static bool TryNormalizeGithubProxy(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!TryCreateHttpUri(value, allowHttps: true, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }

    public static bool TryNormalizeHttpProxy(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!TryCreateHttpUri(value, allowHttps: false, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static bool TryCreateHttpUri(string? value, bool allowHttps, out Uri uri)
    {
        if (Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || allowHttps && parsed.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
