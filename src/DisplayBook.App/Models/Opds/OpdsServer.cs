namespace DisplayBook.App.Models;

public sealed class OpdsServer
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");

	public string Name { get; set; } = string.Empty;

	/// <summary>
	/// Base or OPDS root URL of the server (e.g. http://192.168.0.10:8080/opds/).
	/// </summary>
	public string Url { get; set; } = string.Empty;

	public ServerType Type { get; set; } = ServerType.Manual;

	public DateTime LastSeen { get; set; } = DateTime.MinValue;

	public bool IsEnabled { get; set; } = true;

	/// <summary>
	/// Optional HTTP Basic auth username (empty means no auth).
	/// </summary>
	public string? Username { get; set; }

	/// <summary>
	/// Optional HTTP Basic auth password (stored with the server profile; see security notes in docs).
	/// </summary>
	public string? Password { get; set; }

	/// <summary>
	/// Optional API key sent as the <c>Authorization: Bearer</c> header when present.
	/// </summary>
	public string? ApiKey { get; set; }

	/// <summary>
	/// Arbitrary metadata (e.g. the mDNS service name, host:port).
	/// </summary>
	public Dictionary<string, string> Metadata { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];
}

public sealed class OpdsFeedCacheEntry
{
	public string CacheKey { get; set; } = string.Empty;

	public string Url { get; set; } = string.Empty;

	public string Title { get; set; } = string.Empty;

	public string ParentPath { get; set; } = string.Empty;

	public string? ServerId { get; set; }

	public DateTime CachedAt { get; set; }

	public int EntryCount { get; set; }

	public string SerializedFeed { get; set; } = string.Empty;
}
