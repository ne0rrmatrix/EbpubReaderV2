using DisplayBook.App.Models;

namespace DisplayBook.App.Interfaces;

/// <summary>
/// Persists previously fetched OPDS feeds so the catalog can open instantly and offline.
/// The parser itself is stateless; all feed caching happens here.
/// </summary>
public interface IOpdsCatalogCache
{
	/// <summary>
	/// Cached feed for the given URL, or <c>null</c> when nothing is cached (or the entry is older
	/// than <paramref name="maxAge"/>).
	/// </summary>
	Task<OpdsFeed?> GetAsync(string url, TimeSpan? maxAge = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Stores <paramref name="feed"/> under its self-link (falling back to <paramref name="url"/>).
	/// </summary>
	Task SetAsync(OpdsFeed feed, string url, string? parentPath = null, string? serverId = null, CancellationToken cancellationToken = default);

	/// <summary>
	/// Removes any cached entry whose URL matches.
	/// </summary>
	Task RemoveAsync(string url, CancellationToken cancellationToken = default);

	/// <summary>
	/// Clears all cached feeds.
	/// </summary>
	Task ClearAsync(CancellationToken cancellationToken = default);
}
