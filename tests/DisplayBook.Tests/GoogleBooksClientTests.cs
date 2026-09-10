using System.Net;
using DisplayBook.App.Services.BookMetadata;
using Xunit;

namespace DisplayBook.Tests;

public class GoogleBooksClientTests
{
    private static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BookMetadata", name));

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public int RequestCount { get; private set; }
        public string? LastRequestUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUrl = request.RequestUri?.ToString();
            var response = _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task SearchByIsbn_Found_ParsesAllFields()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, Load("google_books_found.json")));
        var client = new GoogleBooksClient(new HttpClient(handler));

        var result = await client.SearchByIsbnAsync("9780132350884", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Clean Code: A Handbook of Agile Software Craftsmanship", result!.Title);
        Assert.Equal(["Robert C. Martin"], result.Authors);
        Assert.Equal("Prentice Hall", result.Publisher);
        Assert.Equal("2008-08-01", result.PublicationDate);
        Assert.Contains("bring a development organization", result.Description);
        Assert.Equal("0132350882", result.Isbn10);
        Assert.Equal("9780132350884", result.Isbn13);
        Assert.Equal(BookMetadataProvider.GoogleBooks, result.SourceProvider);
        Assert.Contains("books.google.com", result.SourceLink);
    }

    [Fact]
    public async Task SearchByIsbn_RewritesCoverUrlToHttps()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, Load("google_books_found.json")));
        var client = new GoogleBooksClient(new HttpClient(handler));

        var result = await client.SearchByIsbnAsync("9780132350884", null, CancellationToken.None);

        Assert.NotNull(result!.CoverUrl);
        Assert.StartsWith("https://", result.CoverUrl);
    }

    [Fact]
    public async Task SearchByIsbn_AppendsApiKeyWhenProvided()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, Load("google_books_found.json")));
        var client = new GoogleBooksClient(new HttpClient(handler));

        await client.SearchByIsbnAsync("9780132350884", "test-key-123", CancellationToken.None);

        Assert.Contains("key=test-key-123", handler.LastRequestUrl);
    }

    [Fact]
    public async Task SearchByIsbn_OmitsKeyParameterWhenNotProvided()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, Load("google_books_found.json")));
        var client = new GoogleBooksClient(new HttpClient(handler));

        await client.SearchByIsbnAsync("9780132350884", null, CancellationToken.None);

        Assert.DoesNotContain("key=", handler.LastRequestUrl);
    }

    [Fact]
    public async Task SearchByIsbn_TotalItemsZero_ReturnsNull()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, Load("google_books_not_found.json")));
        var client = new GoogleBooksClient(new HttpClient(handler));

        var result = await client.SearchByIsbnAsync("0000000000000", null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchByIsbn_PartialRecord_DegradesMissingFieldsToNull()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, Load("google_books_partial.json")));
        var client = new GoogleBooksClient(new HttpClient(handler));

        var result = await client.SearchByIsbnAsync("0000000000000", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("A Sparse Record", result!.Title);
        Assert.Null(result.Publisher);
        Assert.Null(result.Description);
        Assert.Null(result.CoverUrl);
        Assert.Null(result.Isbn10);
        Assert.Null(result.Isbn13);
    }

    [Fact]
    public async Task SearchByTitleAuthor_BuildsIntitleInauthorQuery()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, Load("google_books_found.json")));
        var client = new GoogleBooksClient(new HttpClient(handler));

        await client.SearchByTitleAuthorAsync("Clean Code", "Robert C. Martin", null, CancellationToken.None);

        // ':' is a reserved character so Uri.EscapeDataString percent-encodes it (%3A);
        // Google's search decodes the query value before parsing "intitle:"/"inauthor:",
        // so this is still a valid, working query — just not literally readable in the URL.
        Assert.Contains("intitle%3AClean+Code", handler.LastRequestUrl);
        Assert.Contains("inauthor%3ARobert+C.+Martin", handler.LastRequestUrl);
    }

    [Fact]
    public async Task FetchJson_RetriesOn429ThenSucceeds()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            Json(HttpStatusCode.OK, Load("google_books_found.json")));
        var client = new GoogleBooksClient(new HttpClient(handler), maxRetries: 3, backoffBaseSeconds: 0.01);

        var result = await client.SearchByIsbnAsync("9780132350884", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FetchJson_ExhaustedRateLimitRetries_ThrowsRateLimited()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var client = new GoogleBooksClient(new HttpClient(handler), maxRetries: 2, backoffBaseSeconds: 0.01);

        await Assert.ThrowsAsync<BookMetadataRateLimitedException>(() =>
            client.SearchByIsbnAsync("9780132350884", null, CancellationToken.None));
    }

    [Fact]
    public async Task FetchJson_RetriesOn5xxThenSucceeds()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            Json(HttpStatusCode.OK, Load("google_books_found.json")));
        var client = new GoogleBooksClient(new HttpClient(handler), maxRetries: 3, backoffBaseSeconds: 0.01);

        var result = await client.SearchByIsbnAsync("9780132350884", null, CancellationToken.None);

        Assert.NotNull(result);
    }
}
