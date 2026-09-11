namespace DisplayBook.App.Services.BookMetadata;

public enum BookMetadataProvider
{
	GoogleBooks,
	OpenLibrary,
}

/// <summary>
/// Provider-agnostic result of a metadata lookup. Neither Google Books nor Open
/// Library return a match-confidence score (unlike the old Amazon search-results
/// scoring) — the review-before-apply diff panel is the safety net for a bad top hit.
/// </summary>
public sealed record FetchedBookMetadata(
	string? Title,
	IReadOnlyList<string> Authors,
	string? Publisher,
	string? PublicationDate,
	string? Description,
	string? Isbn10,
	string? Isbn13,
	string? CoverUrl,
	BookMetadataProvider SourceProvider,
	string? SourceLink);
