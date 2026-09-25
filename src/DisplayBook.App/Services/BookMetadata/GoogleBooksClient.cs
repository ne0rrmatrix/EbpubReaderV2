using System.Text.Json;

namespace DisplayBook.App.Services.BookMetadata;

/// <summary>
/// Thin client over the Google Books "volumes" search endpoint
/// (https://developers.google.com/books/docs/v1/using). Google already ranks
/// results by relevance, so callers just take the top hit — no fuzzy-matching
/// needed, unlike the old Amazon search-results scoring.
/// </summary>
public sealed class GoogleBooksClient(HttpClient httpClient, int maxRetries = 3, double backoffBaseSeconds = 1.0)
{
#pragma warning disable S1075
	const string baseUrl = "https://www.googleapis.com/books/v1/volumes";
#pragma warning restore S1075

	readonly HttpClient httpClient = httpClient;
	readonly int maxRetries = maxRetries;
	readonly double backoffBaseSeconds = backoffBaseSeconds;

	public Task<FetchedBookMetadata?> SearchByIsbnAsync(string isbn, string? apiKey, CancellationToken cancellationToken) =>
		SearchAsync($"isbn:{isbn}", apiKey, cancellationToken);

	public Task<FetchedBookMetadata?> SearchByTitleAuthorAsync(string title, string? author, string? apiKey, CancellationToken cancellationToken)
	{
		string query = string.IsNullOrWhiteSpace(author) ? $"intitle:{title}" : $"intitle:{title} inauthor:{author}";
		return SearchAsync(query, apiKey, cancellationToken);
	}

	async Task<FetchedBookMetadata?> SearchAsync(string query, string? apiKey, CancellationToken cancellationToken)
	{
		string url = BuildUrl(query, apiKey);
		using JsonDocument json = await FetchJsonAsync(url, cancellationToken);
		JsonElement root = json.RootElement;

		int totalItems = root.TryGetProperty("totalItems", out JsonElement totalProp) && totalProp.ValueKind == JsonValueKind.Number
			? totalProp.GetInt32()
			: 0;
		return totalItems <= 0 ||
			!root.TryGetProperty("items", out JsonElement items) ||
			items.ValueKind != JsonValueKind.Array ||
			items.GetArrayLength() == 0
			? null
			: ParseVolume(items[0]);
	}

	static string BuildUrl(string query, string? apiKey)
	{
		string encoded = Uri.EscapeDataString(query).Replace("%20", "+");
		string url = $"{baseUrl}?q={encoded}";
		return string.IsNullOrWhiteSpace(apiKey) ? url : $"{url}&key={Uri.EscapeDataString(apiKey)}";
	}

	async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken cancellationToken)
	{
		Exception? lastException = null;
		for (int attempt = 1; attempt <= maxRetries; attempt++)
		{
			HttpResponseMessage response;
			try
			{
				response = await httpClient.GetAsync(url, cancellationToken);
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
			{
				lastException = exception;
				await BackoffAsync(attempt, cancellationToken);
				continue;
			}

			using (response)
			{
				if ((int)response.StatusCode == 429)
				{
					if (attempt == maxRetries)
					{
						throw new BookMetadataRateLimitedException("Google Books API rate limit exceeded.");
					}

					await BackoffAsync(attempt, cancellationToken);
					continue;
				}

				if ((int)response.StatusCode is >= 500 and < 600)
				{
					lastException = new BookMetadataFetchException($"Google Books returned HTTP {(int)response.StatusCode}.");
					await BackoffAsync(attempt, cancellationToken);
					continue;
				}

				if (!response.IsSuccessStatusCode)
				{
					throw new BookMetadataFetchException($"Google Books returned HTTP {(int)response.StatusCode}.");
				}

				Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
				return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
			}
		}

		throw new BookMetadataFetchException("Google Books request failed after retries.", lastException);
	}

	static List<String> GetAuthors(JsonElement info)
	{
		var authors = new List<string>();
		if (info.ValueKind == JsonValueKind.Object &&
			info.TryGetProperty("authors", out JsonElement authorsProp) &&
			authorsProp.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement author in authorsProp.EnumerateArray())
			{
				if (author.ValueKind == JsonValueKind.String && author.GetString() is { Length: > 0 } name)
				{
					authors.Add(name);
				}
			}
		}
		return authors;
	}
	Task BackoffAsync(int attempt, CancellationToken cancellationToken) =>
		Task.Delay(TimeSpan.FromSeconds(Math.Min(backoffBaseSeconds * attempt, 10)), cancellationToken);

	static FetchedBookMetadata ParseVolume(JsonElement item)
	{
		JsonElement info = item.TryGetProperty("volumeInfo", out JsonElement infoProp) && infoProp.ValueKind == JsonValueKind.Object
			? infoProp
			: default;

		string? GetString(string name) =>
			info.ValueKind == JsonValueKind.Object && info.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
				? value.GetString()
				: null;

		string? title = GetString("title");
		string? subtitle = GetString("subtitle");
		string? fullTitle = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}: {subtitle}";

		var authors = GetAuthors(info);

		string? isbn10 = null;
		string? isbn13 = null;
		if (info.ValueKind == JsonValueKind.Object &&
			info.TryGetProperty("industryIdentifiers", out JsonElement idsProp) &&
			idsProp.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement idElement in idsProp.EnumerateArray())
			{
				isbn10 = BookIsbnExtractor.ExtractIsbn(idElement, "ISBN_10") ?? isbn10;
				isbn13 = BookIsbnExtractor.ExtractIsbn(idElement, "ISBN_13") ?? isbn13;
			}
		}

		string? coverUrl = BookIsbnExtractor.ExtractCoverUrl(info);

		return new FetchedBookMetadata(
			fullTitle,
			authors,
			GetString("publisher"),
			GetString("publishedDate"),
			GetString("description"),
			isbn10,
			isbn13,
			coverUrl,
			BookMetadataProvider.GoogleBooks,
			GetString("infoLink"));
	}


}

public static class BookIsbnExtractor
{
	public static string? ExtractIsbn(JsonElement idElement, string test)
	{
		string? type = ExtractElement(idElement, "type");
		string? value = ExtractElement(idElement, "identifier");

		if (ExtractString(type, test))
		{
			return value;
		}
		return null;
	}
	static bool ExtractString(string? type, string element)
	{
		if (string.Equals(type, element, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return false;
	}
	static string? ExtractElement(JsonElement idElement, string type)
	{
		return idElement.TryGetProperty(type, out JsonElement typeProp) ? typeProp.GetString() : null;
	}
	public static string? ExtractCoverUrl(JsonElement info)
	{
		string? coverUrl = null;
		if (info.ValueKind == JsonValueKind.Object &&
			info.TryGetProperty("imageLinks", out JsonElement imageLinks) &&
			imageLinks.ValueKind == JsonValueKind.Object)
		{
			string? thumbnail = imageLinks.TryGetProperty("thumbnail", out JsonElement t) ? t.GetString() : null;
			string? smallThumbnail = imageLinks.TryGetProperty("smallThumbnail", out JsonElement s) ? s.GetString() : null;
			coverUrl = thumbnail ?? smallThumbnail;
			if (coverUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true)
			{
				coverUrl = "https://" + coverUrl[7..];
			}
		}
		return coverUrl;
	}
}