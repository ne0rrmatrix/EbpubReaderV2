using System.Net;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Models.BookMetadata;
using DisplayBook.App.ViewModels;
using DisplayBook.App.Views;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests.Pages;

/// <summary>
/// The details page's metadata update through the real BookMetadataService: Google Books and Open
/// Library answered from the fixtures DisplayBook.Tests uses, and the cover from the fake network.
/// </summary>
public class BookDetailsPageTests : AppDataTest
{
	static readonly byte[] coverPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

	static string Fixture(string name)
	{
		using Stream stream = typeof(BookDetailsPageTests).Assembly.GetManifestResourceStream($"BookMetadata.{name}")!;
		return new StreamReader(stream).ReadToEnd();
	}

	static void Serve(FakeServer server, HttpStatusCode status, string body = "{}") =>
		server.On(_ => true, _ => FakeServer.Text(status, body));

	static async Task<BookDetailsViewModel> OpenDetailsAsync(string? isbn = "9780132350884")
	{
		BookSummary book = Assert.Single(await TestApp.GetService<IBookImportService>().ImportLocalFileAsync(TestEpub.Write("Clean Code draft", author: "Uncle Bob", isbn: isbn)));
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();
		BookDetailsPage page = await TestApp.GoToAsync<BookDetailsPage>($"Details?id={book.Id}");
		BookDetailsViewModel details = (BookDetailsViewModel)page.BindingContext;
		await Wait.UntilAsync(() => details.Book is not null, "the details page to load the book");
		return details;
	}

	[UIFact]
	public async Task UpdateMetadata_FoundOnGoogleBooks_AppliesItWithTheCoverAndCanBeUndone()
	{
		Serve(FakeNetwork.GoogleBooks, HttpStatusCode.OK, Fixture("google_books_found.json"));
		FakeNetwork.Default.On(_ => true, _ => FakeServer.Bytes(coverPng, "image/png"));
		BookDetailsViewModel details = await OpenDetailsAsync();

		await details.UpdateMetadataCommand.ExecuteAsync(null);
		Assert.Equal(MetadataUpdatePanelState.Found, details.PanelState);
		Assert.Contains(details.ProposedChanges, change => change.IsCover);

		await details.ApplyMetadataCommand.ExecuteAsync(null);

		Assert.Equal(MetadataUpdatePanelState.Applied, details.PanelState);
		Assert.StartsWith("Clean Code", details.Book!.Title);
		Assert.EndsWith(", cover replaced.", details.StatusMessage);
		Assert.True(File.Exists(details.Book.CoverPath));
		Assert.Contains(FakeNetwork.Default.Requests, request => request.Url.StartsWith("https://books.google.com/", StringComparison.Ordinal));

		await details.UndoMetadataCommand.ExecuteAsync(null);
		Assert.Equal("Clean Code draft", details.Book!.Title);
	}

	[UIFact]
	public async Task UpdateMetadata_NotOnGoogleBooks_FallsBackToOpenLibrary()
	{
		Serve(FakeNetwork.GoogleBooks, HttpStatusCode.OK, Fixture("google_books_not_found.json"));
		Serve(FakeNetwork.OpenLibrary, HttpStatusCode.OK, Fixture("open_library_isbn_found.json"));
		BookDetailsViewModel details = await OpenDetailsAsync();

		await details.UpdateMetadataCommand.ExecuteAsync(null);

		Assert.Equal(MetadataUpdatePanelState.Found, details.PanelState);
		Assert.NotEmpty(FakeNetwork.OpenLibrary.Requests);
	}

	[UIFact]
	public async Task UpdateMetadata_NoMatchAnywhere_SaysSo()
	{
		Serve(FakeNetwork.GoogleBooks, HttpStatusCode.OK, Fixture("google_books_not_found.json"));
		Serve(FakeNetwork.OpenLibrary, HttpStatusCode.OK, Fixture("open_library_search_not_found.json"));
		BookDetailsViewModel details = await OpenDetailsAsync(isbn: null);

		await details.UpdateMetadataCommand.ExecuteAsync(null);

		Assert.Equal(MetadataUpdatePanelState.NotFound, details.PanelState);
		Assert.Equal("No match found on Google Books or Open Library.", details.StatusMessage);
	}

	[UIFact]
	public async Task UpdateMetadata_RateLimitedEverywhere_SaysToWait()
	{
		Serve(FakeNetwork.GoogleBooks, HttpStatusCode.TooManyRequests);
		Serve(FakeNetwork.OpenLibrary, HttpStatusCode.TooManyRequests);
		BookDetailsViewModel details = await OpenDetailsAsync();

		await details.UpdateMetadataCommand.ExecuteAsync(null);

		Assert.Equal(MetadataUpdatePanelState.RateLimited, details.PanelState);
	}

	[UIFact]
	public async Task UpdateMetadata_ServersFailing_ReportsTheFailure()
	{
		Serve(FakeNetwork.GoogleBooks, HttpStatusCode.InternalServerError);
		Serve(FakeNetwork.OpenLibrary, HttpStatusCode.InternalServerError);
		BookDetailsViewModel details = await OpenDetailsAsync();

		await details.UpdateMetadataCommand.ExecuteAsync(null);

		Assert.Equal(MetadataUpdatePanelState.NotFound, details.PanelState);
		Assert.False(string.IsNullOrWhiteSpace(details.StatusMessage));
	}

	[UIFact]
	public async Task Apply_CoverDownloadFails_StillAppliesTheRest()
	{
		Serve(FakeNetwork.GoogleBooks, HttpStatusCode.OK, Fixture("google_books_found.json"));
		Serve(FakeNetwork.Default, HttpStatusCode.NotFound);
		BookDetailsViewModel details = await OpenDetailsAsync();
		await details.UpdateMetadataCommand.ExecuteAsync(null);

		await details.ApplyMetadataCommand.ExecuteAsync(null);

		Assert.Equal(MetadataUpdatePanelState.Applied, details.PanelState);
		Assert.DoesNotContain("cover replaced", details.StatusMessage, StringComparison.Ordinal);
	}

	[UIFact]
	public async Task OpenSourceLink_WithNothingFetched_DoesNothing()
	{
		BookDetailsViewModel details = await OpenDetailsAsync();

		await details.OpenSourceLinkCommand.ExecuteAsync(null);

		Assert.Equal(MetadataUpdatePanelState.Idle, details.PanelState);
	}
}
