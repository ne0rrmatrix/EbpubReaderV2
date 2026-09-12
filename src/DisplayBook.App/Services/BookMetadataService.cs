using System.Diagnostics;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services.BookMetadata;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services;

public sealed class BookMetadataService(
	GoogleBooksClient googleBooks,
	OpenLibraryClient openLibrary,
	IHttpClientFactory httpClientFactory,
	ILogger<BookMetadataService> logger) : IBookMetadataService
{

	static readonly string[] array = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

	public async Task<BookMetadataFetchResult> FetchMetadataAsync(BookSummary book, CancellationToken cancellationToken)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		string? apiKey = await GoogleBooksOptions.GetApiKeyAsync();
		string? isbn = string.IsNullOrWhiteSpace(book.Isbn) ? null : book.Isbn;

		List<Exception> failures = [];
		bool anyProviderRespondedCleanly = false;

		async Task<FetchedBookMetadata?> TryProviderAsync(string providerName, Func<Task<FetchedBookMetadata?>> call)
		{
			try
			{
				FetchedBookMetadata? providerResult = await call();
				anyProviderRespondedCleanly = true;
				return providerResult;
			}
			catch (Exception exception) when (exception is BookMetadataRateLimitedException or BookMetadataFetchException or HttpRequestException or TaskCanceledException)
			{
				logger.LogWarning(exception, "{Provider} lookup for {BookId} failed; trying the next source.", providerName, book.Id);
				failures.Add(exception);
				return null;
			}
		}

		FetchedBookMetadata? result = null;
		if (isbn is not null)
		{
			result = await TryProviderAsync("Google Books (ISBN)", () => googleBooks.SearchByIsbnAsync(isbn, apiKey, cancellationToken));
		}

		result ??= await TryProviderAsync("Google Books (search)", () => googleBooks.SearchByTitleAuthorAsync(book.Title, book.Author, apiKey, cancellationToken));

		if (result is null && isbn is not null)
		{
			result = await TryProviderAsync("Open Library (ISBN)", () => openLibrary.SearchByIsbnAsync(isbn, cancellationToken));
		}

		result ??= await TryProviderAsync("Open Library (search)", () => openLibrary.SearchByTitleAuthorAsync(book.Title, book.Author, cancellationToken));

		stopwatch.Stop();

		if (result is not null)
		{
			logger.LogInformation(
				"Metadata lookup for {BookId} matched via {Provider} in {ElapsedMs}ms.",
				book.Id, result.SourceProvider, stopwatch.ElapsedMilliseconds);
			return new BookMetadataFetchResult(BookMetadataFetchStatus.Found, result, stopwatch.Elapsed, null);
		}

		// A source that came back clean (even with no match) is stronger signal than an
		// earlier hiccup elsewhere in the chain — report a genuine "not found" over a stale
		// rate-limit/network error from a source that got successfully worked around.
		if (anyProviderRespondedCleanly || failures.Count == 0)
		{
			logger.LogInformation("Metadata lookup for {BookId} found no match after {ElapsedMs}ms.", book.Id, stopwatch.ElapsedMilliseconds);
			return new BookMetadataFetchResult(BookMetadataFetchStatus.NotFound, null, stopwatch.Elapsed, "No match found on Google Books or Open Library.");
		}

		if (failures.OfType<BookMetadataRateLimitedException>().FirstOrDefault() is { } rateLimited)
		{
			logger.LogWarning("Metadata lookup for {BookId} exhausted every source after {ElapsedMs}ms; last failure was a rate limit.", book.Id, stopwatch.ElapsedMilliseconds);
			return new BookMetadataFetchResult(BookMetadataFetchStatus.RateLimited, null, stopwatch.Elapsed, rateLimited.Message);
		}

		Exception lastFailure = failures[^1];
		logger.LogError(lastFailure, "Metadata lookup for {BookId} failed on every source after {ElapsedMs}ms.", book.Id, stopwatch.ElapsedMilliseconds);
		return new BookMetadataFetchResult(BookMetadataFetchStatus.NetworkError, null, stopwatch.Elapsed, lastFailure.Message);
	}

	public async Task<string> DownloadCoverAsync(string bookId, string coverUrl, CancellationToken cancellationToken)
	{
		string extension = ExtensionFor(coverUrl);
		string relativePath = $"Books/{bookId}/cover.metadata{extension}";
		string destinationPath = BookStorageService.GetAbsolutePath(relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

		HttpClient client = httpClientFactory.CreateClient();
		using HttpResponseMessage response = await client.GetAsync(coverUrl, cancellationToken);
		response.EnsureSuccessStatusCode();
		Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
		await using FileStream file = File.Create(destinationPath);
		await contentStream.CopyToAsync(file, cancellationToken);
		return relativePath;
	}

	static string ExtensionFor(string url)
	{
		string path = url.ToLowerInvariant().Split('?')[0];
		string? match = array
			.FirstOrDefault(ext => path.EndsWith(ext, StringComparison.Ordinal));
		return match switch
		{
			null => ".jpg",
			".jpeg" => ".jpg",
			_ => match,
		};
	}
}