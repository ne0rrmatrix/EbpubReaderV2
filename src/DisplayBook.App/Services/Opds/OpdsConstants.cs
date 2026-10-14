namespace DisplayBook.App.Services.Opds;

/// <summary>
/// Shared constants for the OPDS service layer.
/// </summary>
public static class OpdsConstants
{
	/// <summary>
	/// Name of the shared <c>HttpClient</c> registration used by all OPDS
	/// services (catalog browsing and downloads).
	/// </summary>
	public const string HttpClientName = "opds";

	/// <summary>
	/// Timeout applied to the shared OPDS client. Applies to connect +
	/// response headers; streamed bodies are not bound by this timeout.
	/// </summary>
	public static readonly TimeSpan HttpClientTimeout = TimeSpan.FromSeconds(30);

	/// <summary>Maximum number of downloads allowed in parallel.</summary>
	public const int MaxConcurrentDownloads = 3;
}