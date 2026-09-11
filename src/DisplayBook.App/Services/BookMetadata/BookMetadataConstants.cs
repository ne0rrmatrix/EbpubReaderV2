namespace DisplayBook.App.Services.BookMetadata;

public static class BookMetadataConstants
{
	public const string GoogleBooksClientName = "googlebooks";
	public const string OpenLibraryClientName = "openlibrary";

	public static readonly TimeSpan HttpClientTimeout = TimeSpan.FromSeconds(20);

	/// <summary>
	/// Open Library's docs note a descriptive User-Agent gets a higher courtesy rate
	/// limit; usage here is one user-initiated lookup at a time, well under even the
	/// base limit, so no contact info is included — just enough to not look bare.
	/// </summary>
	public const string OpenLibraryUserAgent = "DisplayBook-EbookReader/1.0";
}
