using DisplayBook.App.Models;
using DisplayBook.App.Services.BookMetadata;

namespace DisplayBook.App.Interfaces;

public enum BookMetadataFetchStatus
{
	Found,
	RateLimited,
	NotFound,
	NetworkError,
}

public sealed record BookMetadataFetchResult(
	BookMetadataFetchStatus Status,
	FetchedBookMetadata? Book,
	TimeSpan Elapsed,
	string? ErrorMessage);

public interface IBookMetadataService
{
	/// <summary>
	/// Looks up <paramref name="book"/> by ISBN first (Google Books, then Open
	/// Library), then falls back to a title+author search on each in turn. Never
	/// throws for expected failure modes (rate-limited, not found, network) — they
	/// come back as a non-<see cref="BookMetadataFetchStatus.Found"/> status instead.
	/// </summary>
	Task<BookMetadataFetchResult> FetchMetadataAsync(BookSummary book, CancellationToken cancellationToken);

	/// <summary>
	/// Downloads <paramref name="coverUrl"/> into the book's storage folder and
	/// returns the new cover's path relative to the content root, ready to pass to
	/// <see cref="IBookCatalogService.UpdateMetadataAsync"/>.
	/// </summary>
	Task<string> DownloadCoverAsync(string bookId, string coverUrl, CancellationToken cancellationToken);
}