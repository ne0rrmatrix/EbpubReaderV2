using System.Text.Json;

namespace DisplayBook.App.Services.BookMetadata;

/// <summary>
/// Thin client over Open Library's fully keyless APIs
/// (https://openlibrary.org/dev/docs/api/books). ISBN lookups deliberately use the
/// "legacy" <c>/api/books?...&amp;jscmd=data</c> endpoint rather than the newer
/// <c>/isbn/{isbn}.json</c>: <c>jscmd=data</c> resolves author names inline, avoiding
/// an extra round-trip per author to <c>/authors/{key}.json</c>. It's flagged
/// "may be phased out" in the docs but has carried that flag for years; the
/// dependency is isolated entirely inside this one method, cheap to swap if it
/// ever actually breaks.
/// </summary>
public sealed class OpenLibraryClient
{
    private readonly HttpClient _httpClient;
    private readonly int _maxRetries;
    private readonly double _backoffBaseSeconds;

    public OpenLibraryClient(HttpClient httpClient, int maxRetries = 3, double backoffBaseSeconds = 1.0)
    {
        _httpClient = httpClient;
        _maxRetries = maxRetries;
        _backoffBaseSeconds = backoffBaseSeconds;
    }

    public async Task<FetchedBookMetadata?> SearchByIsbnAsync(string isbn, CancellationToken cancellationToken)
    {
        var url = $"https://openlibrary.org/api/books?bibkeys=ISBN:{Uri.EscapeDataString(isbn)}&format=json&jscmd=data";
        using var json = await FetchJsonAsync(url, cancellationToken);
        var bibkey = $"ISBN:{isbn}";
        if (!json.RootElement.TryGetProperty(bibkey, out var record) || record.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ParseIsbnRecord(record, isbn);
    }

    public async Task<FetchedBookMetadata?> SearchByTitleAuthorAsync(string title, string? author, CancellationToken cancellationToken)
    {
        var url = $"https://openlibrary.org/search.json?title={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrWhiteSpace(author))
        {
            url += $"&author={Uri.EscapeDataString(author)}";
        }

        using var json = await FetchJsonAsync(url, cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array || docs.GetArrayLength() == 0)
        {
            return null;
        }

        return ParseSearchDoc(docs[0]);
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
                        throw new BookMetadataRateLimitedException("Open Library rate limit exceeded.");
                    }

                    await BackoffAsync(attempt, cancellationToken);
                    continue;
                }

                if ((int)response.StatusCode is >= 500 and < 600)
                {
                    lastException = new BookMetadataFetchException($"Open Library returned HTTP {(int)response.StatusCode}.");
                    await BackoffAsync(attempt, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new BookMetadataFetchException($"Open Library returned HTTP {(int)response.StatusCode}.");
                }

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
        }

        throw new BookMetadataFetchException("Open Library request failed after retries.", lastException);
    }

    private Task BackoffAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Min(_backoffBaseSeconds * attempt, 10)), cancellationToken);

    private static FetchedBookMetadata ParseIsbnRecord(JsonElement record, string queriedIsbn)
    {
        string? GetString(string name) =>
            record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var title = GetString("title");
        var subtitle = GetString("subtitle");
        var fullTitle = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}: {subtitle}";

        var authors = new List<string>();
        if (record.TryGetProperty("authors", out var authorsProp) && authorsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var author in authorsProp.EnumerateArray())
            {
                if (author.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } name)
                {
                    authors.Add(name);
                }
            }
        }

        string? publisher = null;
        if (record.TryGetProperty("publishers", out var publishersProp) && publishersProp.ValueKind == JsonValueKind.Array && publishersProp.GetArrayLength() > 0)
        {
            publisher = publishersProp[0].TryGetProperty("name", out var pubName) ? pubName.GetString() : null;
        }

        var (isbn10, isbn13) = ExtractIsbns(record, queriedIsbn);
        var coverUrl = ExtractCoverUrl(record, queriedIsbn);
        var sourceLink = GetString("url");

        return new FetchedBookMetadata(
            fullTitle,
            authors,
            publisher,
            GetString("publish_date"),
            ExtractDescription(record),
            isbn10,
            isbn13,
            coverUrl,
            BookMetadataProvider.OpenLibrary,
            sourceLink);
    }

    private static FetchedBookMetadata ParseSearchDoc(JsonElement doc)
    {
        string? GetString(string name) =>
            doc.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        var authors = new List<string>();
        if (doc.TryGetProperty("author_name", out var authorNames) && authorNames.ValueKind == JsonValueKind.Array)
        {
            foreach (var author in authorNames.EnumerateArray())
            {
                if (author.ValueKind == JsonValueKind.String && author.GetString() is { Length: > 0 } name)
                {
                    authors.Add(name);
                }
            }
        }

        string? isbn10 = null;
        string? isbn13 = null;
        if (doc.TryGetProperty("isbn", out var isbnArray) && isbnArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var isbnEl in isbnArray.EnumerateArray())
            {
                var value = isbnEl.GetString();
                if (value is null)
                {
                    continue;
                }

                if (value.Length == 13 && isbn13 is null)
                {
                    isbn13 = value;
                }
                else if (value.Length == 10 && isbn10 is null)
                {
                    isbn10 = value;
                }
            }
        }

        string? publisher = null;
        if (doc.TryGetProperty("publisher", out var publisherArray) && publisherArray.ValueKind == JsonValueKind.Array && publisherArray.GetArrayLength() > 0)
        {
            publisher = publisherArray[0].GetString();
        }

        string? coverUrl = null;
        if (doc.TryGetProperty("cover_i", out var coverIdProp) && coverIdProp.ValueKind == JsonValueKind.Number)
        {
            coverUrl = $"https://covers.openlibrary.org/b/id/{coverIdProp.GetInt32()}-L.jpg?default=false";
        }

        var publishYear = doc.TryGetProperty("first_publish_year", out var yearProp) && yearProp.ValueKind == JsonValueKind.Number
            ? yearProp.GetInt32().ToString()
            : null;

        string? sourceLink = doc.TryGetProperty("key", out var keyProp) && keyProp.GetString() is { Length: > 0 } key
            ? $"https://openlibrary.org{key}"
            : null;

        return new FetchedBookMetadata(
            GetString("title"),
            authors,
            publisher,
            publishYear,
            null,
            isbn10,
            isbn13,
            coverUrl,
            BookMetadataProvider.OpenLibrary,
            sourceLink);
    }

    private static (string? Isbn10, string? Isbn13) ExtractIsbns(JsonElement record, string queriedIsbn)
    {
        string? isbn10 = null;
        string? isbn13 = null;
        if (record.TryGetProperty("identifiers", out var identifiers) && identifiers.ValueKind == JsonValueKind.Object)
        {
            if (identifiers.TryGetProperty("isbn_10", out var isbn10Array) && isbn10Array.ValueKind == JsonValueKind.Array && isbn10Array.GetArrayLength() > 0)
            {
                isbn10 = isbn10Array[0].GetString();
            }

            if (identifiers.TryGetProperty("isbn_13", out var isbn13Array) && isbn13Array.ValueKind == JsonValueKind.Array && isbn13Array.GetArrayLength() > 0)
            {
                isbn13 = isbn13Array[0].GetString();
            }
        }

        var cleanedQuery = BookIdentifiers.Clean(queriedIsbn);
        if (isbn13 is null && BookIdentifiers.IsIsbn13(cleanedQuery))
        {
            isbn13 = cleanedQuery;
        }
        else if (isbn10 is null && BookIdentifiers.IsIsbn10(cleanedQuery))
        {
            isbn10 = cleanedQuery;
        }

        return (isbn10, isbn13);
    }

    private static string? ExtractCoverUrl(JsonElement record, string queriedIsbn)
    {
        if (record.TryGetProperty("cover", out var cover) && cover.ValueKind == JsonValueKind.Object)
        {
            foreach (var size in new[] { "large", "medium", "small" })
            {
                if (cover.TryGetProperty(size, out var url) && url.ValueKind == JsonValueKind.String)
                {
                    return url.GetString();
                }
            }
        }

        return $"https://covers.openlibrary.org/b/isbn/{Uri.EscapeDataString(queriedIsbn)}-L.jpg?default=false";
    }

    /// <summary>
    /// Open Library editions rarely carry a description at all, and when present
    /// (mainly on work-level records elsewhere in the API) it can be either a plain
    /// string or a <c>{ type, value }</c> object — handle both, degrade to null.
    /// </summary>
    private static string? ExtractDescription(JsonElement record)
    {
        if (!record.TryGetProperty("description", out var description))
        {
            return null;
        }

        return description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Object when description.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }
}
