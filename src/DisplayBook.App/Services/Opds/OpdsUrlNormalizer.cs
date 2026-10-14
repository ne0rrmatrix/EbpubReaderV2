namespace DisplayBook.App.Services;

/// <summary>
/// Produces a canonical form of an OPDS URL for identity comparisons and cache keys
/// (trailing slashes, query strings and fragments ignored).
/// </summary>
static class OpdsUrlNormalizer
{
	public static string Normalize(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return string.Empty;
		}

		string trimmed = url.Trim();
		int queryIndex = trimmed.IndexOf('?');
		if (queryIndex >= 0)
		{
			trimmed = trimmed[..queryIndex];
		}

		int fragmentIndex = trimmed.IndexOf('#');
		if (fragmentIndex >= 0)
		{
			trimmed = trimmed[..fragmentIndex];
		}

		return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
			&& (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			? $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}"
			: trimmed.TrimEnd('/');
	}
}