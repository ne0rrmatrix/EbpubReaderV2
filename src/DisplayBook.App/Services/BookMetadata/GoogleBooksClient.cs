using System.Text.Json;

namespace DisplayBook.App.Services.BookMetadata;

/// <summary>
/// Thin client over the Google Books "volumes" search endpoint
/// (https://developers.google.com/books/docs/v1/using). Google already ranks
/// results by relevance, so callers just take the top hit — no fuzzy-matching
/// needed, unlike the old Amazon search-results scoring.
/// </summary>
public sealed class GoogleBooksClient
{
    private const string BaseUrl = "https://www.googleapis.com/books/v1/volumes";

    private readonly HttpClient _httpClient;
    private readonly int _maxRetries;
    private readonly double _backoffBaseSeconds;

    public GoogleBooksClient(HttpClient httpClient, int maxRetries = 3, double backoffBaseSeconds = 1.0)
    {
        _httpClient = httpClient;
        _maxRetries = maxRetries;
        _backoffBaseSeconds = backoffBaseSeconds;
    }

    public Task<FetchedBookMetadata?> SearchByIsbnAsync(string isbn, string? apiKey, CancellationToken cancellationToken) =>
        SearchAsync($"isbn:{isbn}", apiKey, cancellationToken);

    public Task<FetchedBookMetadata?> SearchByTitleAuthorAsync(string title, string? author, string? apiKey, CancellationToken cancellationToken)
    {
        var query = string.IsNullOrWhiteSpace(author) ? $"intitle:{title}" : $"intitle:{title} inauthor:{author}";
        return SearchAsync(query, apiKey, cancellationToken);
    }

    private async Task<FetchedBookMetadata?> SearchAsync(string query, string? apiKey, CancellationToken cancellationToken)
    {
        var url = BuildUrl(query, apiKey);
        using var json = await FetchJsonAsync(url, cancellationToken);
        var root = json.RootElement;

        var totalItems = root.TryGetProperty("totalItems", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number
            ? totalProp.GetInt32()
            : 0;
        if (totalItems <= 0 ||
            !root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() == 0)
        {
            return null;
        }

        return ParseVolume(items[0]);
    }

    private static string BuildUrl(string query, string? apiKey)
    {
        var encoded = Uri.EscapeDataString(query).Replace("%20", "+");
        var url = $"{BaseUrl}?q={encoded}";
        return string.IsNullOrWhiteSpace(apiKey) ? url : $"{url}&key={Uri.EscapeDataString(apiKey)}";
    }

    private async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= _maxRetries; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url, cancellationToken);
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
                    if (attempt == _maxRetries)
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

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
        }

        throw new BookMetadataFetchException("Google Books request failed after retries.", lastException);
    }

    private Task BackoffAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Min(_backoffBaseSeconds * attempt, 10)), cancellationToken);

    private static FetchedBookMetadata ParseVolume(JsonElement item)
    {
        var info = item.TryGetProperty("volumeInfo", out var infoProp) && infoProp.ValueKind == JsonValueKind.Object
            ? infoProp
            : default;

        string? GetString(string name) =>
            info.ValueKind == JsonValueKind.Object && info.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        var title = GetString("title");
        var subtitle = GetString("subtitle");
        var fullTitle = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}: {subtitle}";

        var authors = new List<string>();
        if (info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("authors", out var authorsProp) &&
            authorsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var author in authorsProp.EnumerateArray())
            {
                if (author.ValueKind == JsonValueKind.String && author.GetString() is { Length: > 0 } name)
                {
                    authors.Add(name);
                }
            }
        }

        string? isbn10 = null;
        string? isbn13 = null;
        if (info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("industryIdentifiers", out var idsProp) &&
            idsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var idElement in idsProp.EnumerateArray())
            {
                var type = idElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                var value = idElement.TryGetProperty("identifier", out var valueProp) ? valueProp.GetString() : null;
                if (value is null)
                {
                    continue;
                }

                if (string.Equals(type, "ISBN_13", StringComparison.OrdinalIgnoreCase))
                {
                    isbn13 = value;
                }
                else if (string.Equals(type, "ISBN_10", StringComparison.OrdinalIgnoreCase))
                {
                    isbn10 = value;
                }
            }
        }

        string? coverUrl = null;
        if (info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("imageLinks", out var imageLinks) &&
            imageLinks.ValueKind == JsonValueKind.Object)
        {
            var thumbnail = imageLinks.TryGetProperty("thumbnail", out var t) ? t.GetString() : null;
            var smallThumbnail = imageLinks.TryGetProperty("smallThumbnail", out var s) ? s.GetString() : null;
            coverUrl = thumbnail ?? smallThumbnail;
            if (coverUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true)
            {
                coverUrl = "https://" + coverUrl[7..];
            }
        }

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
