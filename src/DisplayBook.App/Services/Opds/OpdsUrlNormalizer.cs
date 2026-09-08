namespace DisplayBook.App.Services;

/// <summary>
/// Produces a canonical form of an OPDS URL for identity comparisons and cache keys
/// (trailing slashes, query strings and fragments ignored).
/// </summary>
internal static class OpdsUrlNormalizer
{
    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        var queryIndex = trimmed.IndexOf('?');
        if (queryIndex >= 0)
        {
            trimmed = trimmed[..queryIndex];
        }

        var fragmentIndex = trimmed.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            trimmed = trimmed[..fragmentIndex];
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        }

        return trimmed.TrimEnd('/');
    }
}
