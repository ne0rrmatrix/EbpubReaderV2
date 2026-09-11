using System.Net;
using System.Text;
using DisplayBook.App.Services.BookMetadata;
using Xunit;

namespace DisplayBook.Tests;

public class OpenLibraryClientTests
{
	static string Load(string name) =>
		File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BookMetadata", name));

	sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
	{
		readonly Queue<HttpResponseMessage> responses = new(responses);

		public int RequestCount { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			RequestCount++;
			HttpResponseMessage response = responses.Count > 0 ? responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
			return Task.FromResult(response);
		}
	}

	static HttpResponseMessage Json(HttpStatusCode status, string body) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	[Fact]
	public async Task SearchByIsbn_Found_ParsesFieldsIncludingObjectDescription()
	{
		QueueHandler handler = new(Json(HttpStatusCode.OK, Load("open_library_isbn_found.json")));
		OpenLibraryClient client = new(new HttpClient(handler));

		FetchedBookMetadata? result = await client.SearchByIsbnAsync("9780132350884", CancellationToken.None);

		Assert.NotNull(result);
		Assert.Equal("Clean Code: A Handbook of Agile Software Craftsmanship", result.Title);
		Assert.Equal(["Robert C. Martin"], result.Authors);
		Assert.Equal("Prentice Hall", result.Publisher);
		Assert.Equal("2008", result.PublicationDate);
		Assert.Equal("0132350882", result.Isbn10);
		Assert.Equal("9780132350884", result.Isbn13);
		Assert.Equal("https://covers.openlibrary.org/b/id/6790108-L.jpg", result.CoverUrl);
		Assert.Equal(BookMetadataProvider.OpenLibrary, result.SourceProvider);
		Assert.Contains("openlibrary.org", result.SourceLink);
		Assert.Contains("agile software craftsmanship", result.Description);
	}

	[Fact]
	public async Task SearchByIsbn_PlainStringDescription_IsParsed()
	{
		QueueHandler handler = new(Json(HttpStatusCode.OK, Load("open_library_isbn_string_description.json")));
		OpenLibraryClient client = new(new HttpClient(handler));

		FetchedBookMetadata? result = await client.SearchByIsbnAsync("9780132350884", CancellationToken.None);

		Assert.NotNull(result);
		Assert.Equal("A plain string description, the other shape Open Library returns.", result.Description);
	}

	[Fact]
	public async Task SearchByIsbn_UnknownBibkey_ReturnsNull()
	{
		QueueHandler handler = new(Json(HttpStatusCode.OK, Load("open_library_isbn_not_found.json")));
		OpenLibraryClient client = new(new HttpClient(handler));

		FetchedBookMetadata? result = await client.SearchByIsbnAsync("0000000000000", CancellationToken.None);

		Assert.Null(result);
	}

	[Fact]
	public async Task SearchByIsbn_MissingCoverBlock_FallsBackToCoversEndpoint()
	{
		QueueHandler handler = new(Json(HttpStatusCode.OK, Load("open_library_isbn_string_description.json")));
		OpenLibraryClient client = new(new HttpClient(handler));

		FetchedBookMetadata? result = await client.SearchByIsbnAsync("9780132350884", CancellationToken.None);

		Assert.NotNull(result);
		Assert.Equal("https://covers.openlibrary.org/b/isbn/9780132350884-L.jpg?default=false", result.CoverUrl);
	}

	[Fact]
	public async Task SearchByTitleAuthor_Found_ParsesInlineAuthorNames()
	{
		QueueHandler handler = new(Json(HttpStatusCode.OK, Load("open_library_search_found.json")));
		OpenLibraryClient client = new(new HttpClient(handler));

		FetchedBookMetadata? result = await client.SearchByTitleAuthorAsync("Clean Code", "Robert C. Martin", CancellationToken.None);

		Assert.NotNull(result);
		Assert.Equal("Clean Code", result.Title);
		Assert.Equal(["Robert C. Martin"], result.Authors);
		Assert.Equal("Prentice Hall", result.Publisher);
		Assert.Equal("2008", result.PublicationDate);
		Assert.Equal("9780132350884", result.Isbn13);
		Assert.Equal("0132350882", result.Isbn10);
		Assert.Equal("https://covers.openlibrary.org/b/id/6790108-L.jpg?default=false", result.CoverUrl);
		Assert.Null(result.Description);
	}

	[Fact]
	public async Task SearchByTitleAuthor_NoResults_ReturnsNull()
	{
		QueueHandler handler = new(Json(HttpStatusCode.OK, Load("open_library_search_not_found.json")));
		OpenLibraryClient client = new(new HttpClient(handler));

		FetchedBookMetadata? result = await client.SearchByTitleAuthorAsync("Totally Unknown Title", "Nobody", CancellationToken.None);

		Assert.Null(result);
	}

	[Fact]
	public async Task FetchJson_RetriesOn429ThenSucceeds()
	{
		QueueHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.TooManyRequests),
			Json(HttpStatusCode.OK, Load("open_library_isbn_found.json")));
		OpenLibraryClient client = new(new HttpClient(handler), maxRetries: 3, backoffBaseSeconds: 0.01);

		FetchedBookMetadata? result = await client.SearchByIsbnAsync("9780132350884", CancellationToken.None);

		Assert.NotNull(result);
		Assert.Equal(2, handler.RequestCount);
	}

	[Fact]
	public async Task FetchJson_ExhaustedRateLimitRetries_ThrowsRateLimited()
	{
		QueueHandler handler = new(
			new HttpResponseMessage(HttpStatusCode.TooManyRequests),
			new HttpResponseMessage(HttpStatusCode.TooManyRequests));
		OpenLibraryClient client = new(new HttpClient(handler), maxRetries: 2, backoffBaseSeconds: 0.01);

		await Assert.ThrowsAsync<BookMetadataRateLimitedException>(() =>
			client.SearchByIsbnAsync("9780132350884", CancellationToken.None));
	}
}
