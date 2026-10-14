using System.Net;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.ViewModels;
using DisplayBook.App.Views;
using DisplayBook.DeviceTests.Support;
using DisplayBook.Viewer.Controls;
using DisplayBook.Viewer.Models;

namespace DisplayBook.DeviceTests.Tests.Pages;

/// <summary>
/// The reader end to end: the real ReaderPage in the app shell, its EpubReaderView hosting the
/// real WebView and JavaScript reader, and the bridge between them. Bridge messages are sent from
/// JavaScript exactly the way the reader sends them, by navigating to displaybook://bridge.
/// Synced reading positions come from the fake Firebase server.
/// </summary>
public class ReaderPageTests : AppDataTest
{
	static readonly TimeSpan readerTimeout = TimeSpan.FromSeconds(60);

	sealed record OpenReader(ReaderPage Page, EpubReaderView Reader, ReaderViewModel ViewModel, BookSummary Book);

	static async Task<BookSummary> ImportBookAsync(string title = "Reader Test Book")
	{
		IReadOnlyList<BookSummary> imported = await TestApp.GetService<IBookImportService>().ImportLocalFileAsync(TestEpub.Write(title, chapters: 4));
		return Assert.Single(imported);
	}

	static async Task<OpenReader> OpenAsync(BookSummary book)
	{
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();
		ReaderPage page = await TestApp.GoToAsync<ReaderPage>($"reader?id={book.Id}");
		return await WaitForReaderAsync(page, book);
	}

	static async Task<OpenReader> WaitForReaderAsync(ReaderPage page, BookSummary book)
	{
		EpubReaderView reader = page.FindByName<EpubReaderView>("Reader");
		ReaderViewModel viewModel = (ReaderViewModel)page.BindingContext;
		View overlay = reader.FindByName<View>("LoadingOverlay");
		string? error = null;
		reader.ReaderError += (_, message) => error ??= message;
		await Wait.UntilAsync(
			() => error is not null || (viewModel.Book?.Id == book.Id && !overlay.IsVisible),
			"the reader to open the book",
			readerTimeout);
		Assert.True(error is null, $"The reader reported an error: {error}");

		// A book opened with no saved position reports none of its own: EpubText.js sends its first
		// locationChanged and readerReady back to back, the second bridge navigation replaces the
		// first, and with no start locator readerReady alone completes the handshake. So ask for the
		// first chapter, which every later step here needs a reported position for.
		if (string.IsNullOrEmpty(viewModel.Locator.ResourceHref))
		{
			await reader.SetLocatorAsync(new EpubLocator(Chapter(1), 0, 1));
		}

		await Wait.UntilAsync(() => !string.IsNullOrEmpty(viewModel.Locator.ResourceHref), "the reader to report its position", readerTimeout);
		return new OpenReader(page, reader, viewModel, book);
	}

	static Task SendBridgeMessageAsync(EpubReaderView reader, string type, string payloadJson = "{}") =>
		NavigateBridgeAsync(reader, $"'?message=' + encodeURIComponent(JSON.stringify({{ type: '{type}', payload: {payloadJson} }}))");

	static Task NavigateBridgeAsync(EpubReaderView reader, string queryExpression) =>
		reader.FindByName<WebView>("ReaderWebView").EvaluateJavaScriptAsync($"window.location.href = 'displaybook://bridge' + {queryExpression}; 'sent'");

	static async Task<BookSummary?> SavedBookAsync(string bookId) =>
		await TestApp.GetService<IBookCatalogService>().GetBookAsync(bookId);

	static string Chapter(int index) => $"OEBPS/Text/ch{index}.xhtml";

	/// <summary>
	/// Waits for a book opened at a saved position to reach it. The reader lays out the first
	/// chapter before moving there, and when that first chapter's position report arrives after
	/// readerReady (on a slow machine), EpubReaderView takes it as the answer to the start locator:
	/// the loading overlay clears on chapter 1 and the requested chapter follows a moment later.
	/// </summary>
	static Task WaitForChapterAsync(OpenReader open, int index) =>
		Wait.UntilAsync(() => open.ViewModel.Locator.ResourceHref == Chapter(index), $"the reader to reach chapter {index}", readerTimeout);

	/// <summary>
	/// The app losing and regaining focus. ReaderPage listens on its own window, or on the app's
	/// first window when its own isn't set yet as it appears; the app has only one window, but
	/// under test the first is the runner's, and raising focus events on the runner's window
	/// breaks the runner. So the page's two focus handlers are called directly instead.
	/// </summary>
	static void LoseFocus(ReaderPage page) => InvokeFocusHandler(page, "OnWindowDeactivated");

	static void RegainFocus(ReaderPage page) => InvokeFocusHandler(page, "OnWindowActivated");

	static void InvokeFocusHandler(ReaderPage page, string handler) =>
		typeof(ReaderPage)
			.GetMethod(handler, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
			.Invoke(page, [null, EventArgs.Empty]);

	[UIFact]
	public async Task OpenFromDetails_ShowsTheBookAndSavesTheReadingPosition()
	{
		BookSummary book = await ImportBookAsync();
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();

		BookDetailsPage details = await TestApp.GoToAsync<BookDetailsPage>($"Details?id={book.Id}");
		BookDetailsViewModel detailsViewModel = (BookDetailsViewModel)details.BindingContext;
		await Wait.UntilAsync(() => detailsViewModel.Book is not null, "the details page to load the book");
		await detailsViewModel.OpenCommand.ExecuteAsync(null);
		OpenReader open = await WaitForReaderAsync(await TestApp.WaitForPageAsync<ReaderPage>(), book);

		Assert.Equal(Chapter(1), open.ViewModel.Locator.ResourceHref);
		await Wait.UntilAsync(() => SavedBookAsync(book.Id).Result?.LocatorResourceHref == Chapter(1), "the position to be saved");
		Assert.NotNull((await SavedBookAsync(book.Id))!.LastOpenedAt);
	}

	[UIFact]
	public async Task SetLocator_MovesToAnotherChapterAndSavesIt()
	{
		OpenReader open = await OpenAsync(await ImportBookAsync());

		await open.Reader.SetLocatorAsync(new EpubLocator(Chapter(3), 0, 1));

		await Wait.UntilAsync(() => open.ViewModel.Locator.ResourceHref == Chapter(3), "the reader to move to chapter 3", readerTimeout);
		await Wait.UntilAsync(() => SavedBookAsync(open.Book.Id).Result?.LocatorResourceHref == Chapter(3), "chapter 3 to be saved");
	}

	[UIFact]
	public async Task Reopening_ResumesAtTheSavedPosition()
	{
		BookSummary book = await ImportBookAsync();
		await TestApp.GetService<IBookCatalogService>().SaveLocatorAsync(book.Id, Chapter(2), 0, 1);

		OpenReader open = await OpenAsync(book);

		await WaitForChapterAsync(open, 2);
	}

	[UIFact]
	public async Task OpeningASecondBook_ReusesTheLoadedReader()
	{
		await OpenAsync(await ImportBookAsync("First Book"));

		OpenReader second = await OpenAsync(await ImportBookAsync("Second Book"));

		Assert.Equal("Second Book", second.ViewModel.Book!.Title);
		Assert.Equal(Chapter(1), second.ViewModel.Locator.ResourceHref);
	}

	[UIFact]
	public async Task BridgeMessages_RaiseTheMatchingEvents()
	{
		OpenReader open = await OpenAsync(await ImportBookAsync());
		List<string> themes = [];
		List<bool> chrome = [];
		List<string> errors = [];
		int settingsRequests = 0;
		open.Reader.ThemeChanged += (_, theme) => themes.Add(theme);
		open.Reader.ChromeVisibilityChanged += (_, visible) => chrome.Add(visible);
		open.Reader.ReaderError += (_, error) => errors.Add(error);
		open.Reader.SettingsRequested += (_, _) => settingsRequests++;

		await SendBridgeMessageAsync(open.Reader, "themeChanged", "{ theme: 'night' }");
		await SendBridgeMessageAsync(open.Reader, "themeChanged", "{ theme: '' }");
		await SendBridgeMessageAsync(open.Reader, "chromeVisibilityChanged", "{ visible: true }");
		await SendBridgeMessageAsync(open.Reader, "chromeVisibilityChanged", "{ visible: 'yes' }");
		await SendBridgeMessageAsync(open.Reader, "requestSettings");
		await SendBridgeMessageAsync(open.Reader, "readerError", "{ message: 'Chapter failed to render.' }");
		await SendBridgeMessageAsync(open.Reader, "readerError", "{ message: '' }");
		await SendBridgeMessageAsync(open.Reader, "openExternalLink", "{ url: 'ftp://example.invalid/book' }");
		await SendBridgeMessageAsync(open.Reader, "somethingNew");
		await NavigateBridgeAsync(open.Reader, "'?message=' + encodeURIComponent('{ not json')");
		await NavigateBridgeAsync(open.Reader, "'?other=1'");

		await Wait.UntilAsync(() => errors.Count == 4 && themes.Count == 1 && chrome.Count == 1 && settingsRequests == 1, "every bridge message to be handled");
		Assert.Equal(["night"], themes);
		Assert.Equal([true], chrome);
		Assert.Equal("Chapter failed to render.", errors[0]);
		Assert.Equal("The reader could not open the publication.", errors[1]);
		Assert.StartsWith("The reader bridge sent invalid JSON", errors[2]);
		Assert.Equal("The reader bridge sent an empty message.", errors[3]);
	}

	[UIFact]
	public async Task DictionaryLookup_ShowsTheDefinitionAndCloseHidesIt()
	{
		OpenReader open = await OpenAsync(await ImportBookAsync());
		View definition = open.Reader.FindByName<View>("DefinitionOverlay");
		Label word = open.Reader.FindByName<Label>("DefinitionWordLabel");
		Label text = open.Reader.FindByName<Label>("DefinitionTextLabel");

		await SendBridgeMessageAsync(open.Reader, "dictionaryLookupRequested", "{ text: 'Book,' }");
		await Wait.UntilAsync(() => definition.IsVisible, "the definition card to open");
		Assert.Equal("book", word.Text, ignoreCase: true);
		Assert.False(string.IsNullOrWhiteSpace(text.Text));

		Button close = definition.GetVisualTreeDescendants().OfType<Button>().First();
		((IButtonController)close).SendClicked();
		await Wait.UntilAsync(() => !definition.IsVisible, "the definition card to close");

		await SendBridgeMessageAsync(open.Reader, "dictionaryLookupRequested", "{ text: 'zzqxqz' }");
		await Wait.UntilAsync(() => definition.IsVisible, "the not-found card to open");
		Assert.Equal("No definition found for “zzqxqz”.", text.Text);
	}

	[UIFact]
	public async Task RequestExit_ReturnsToThePreviousPage()
	{
		OpenReader open = await OpenAsync(await ImportBookAsync());

		await SendBridgeMessageAsync(open.Reader, "requestExit");

		await TestApp.WaitForPageAsync<LibraryPage>();
	}

	[UIFact]
	public async Task WindowLifecycle_SignedOut_KeepsTheReaderOpenWithoutPrompting()
	{
		OpenReader open = await OpenAsync(await ImportBookAsync());
		LoseFocus(open.Page);
		RegainFocus(open.Page);
		((IWindow)open.Page.Window!).Resumed();
		await Task.Delay(500);

		Assert.Empty(TestDialogs.Alerts);
		Assert.Same(open.Page, (await TestApp.GetShellAsync()).CurrentPage);
		Assert.False(open.Reader.FindByName<View>("LoadingOverlay").IsVisible);
	}

	// --- Synced reading positions (signed in against the fake Firebase server) ---

	static async Task SignInAsync()
	{
		FakeNetwork.Firebase.On(
			request => request.RequestUri!.AbsoluteUri.Contains("accounts:signInWithPassword", StringComparison.Ordinal),
			_ => FakeServer.Text(HttpStatusCode.OK, """{ "idToken": "id-1", "refreshToken": "refresh-1", "localId": "user-1", "expiresIn": "3600" }"""));
		FirebaseAuthResult result = await TestApp.GetService<IFirebaseAuthService>().SignInAsync("reader@example.com", "secret");
		Assert.True(result.Succeeded, result.ErrorMessage);
	}

	static void ServeRemotePosition(string resourceHref, DateTimeOffset updatedAt) =>
		FakeNetwork.Firebase.On(
			request => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/positions/", StringComparison.Ordinal),
			_ => FakeServer.Text(HttpStatusCode.OK, $$"""
				{ "fields": {
					"resourceHref": { "stringValue": "{{resourceHref}}" },
					"charOffset": { "integerValue": "0" },
					"page": { "integerValue": "0" },
					"pageCount": { "integerValue": "1" },
					"updatedAt": { "timestampValue": "{{updatedAt.UtcDateTime:O}}" } } }
				"""));

	[UIFact]
	public async Task SyncedPosition_NewerOnAnotherDevice_AdvancesWhenAccepted()
	{
		await SignInAsync();
		BookSummary book = await ImportBookAsync();
		await TestApp.GetService<IBookCatalogService>().SaveLocatorAsync(book.Id, Chapter(1), 0, 1);
		ServeRemotePosition(Chapter(3), DateTimeOffset.UtcNow.AddHours(1));
		TestDialogs.AnswerNextAlert(accept: true);

		OpenReader open = await OpenAsync(book);

		Assert.Equal("Continue from another device?", Assert.Single(TestDialogs.Alerts).Title);
		await WaitForChapterAsync(open, 3);
	}

	[UIFact]
	public async Task SyncedPosition_OlderOnAnotherDevice_StaysWhenDeclined()
	{
		await SignInAsync();
		BookSummary book = await ImportBookAsync();
		await TestApp.GetService<IBookCatalogService>().SaveLocatorAsync(book.Id, Chapter(2), 0, 1);
		ServeRemotePosition(Chapter(4), DateTimeOffset.UtcNow.AddDays(-1));

		OpenReader open = await OpenAsync(book);

		Assert.Equal("Different reading position found", Assert.Single(TestDialogs.Alerts).Title);
		await WaitForChapterAsync(open, 2);
	}

	[UIFact]
	public async Task SyncedPosition_FirstOpenOnThisDevice_AdoptedWithoutPrompting()
	{
		await SignInAsync();
		BookSummary book = await ImportBookAsync();
		ServeRemotePosition(Chapter(2), DateTimeOffset.UtcNow.AddHours(-1));

		OpenReader open = await OpenAsync(book);

		await WaitForChapterAsync(open, 2);
		Assert.Empty(TestDialogs.Alerts);
	}

	[UIFact]
	public async Task SyncedPosition_NewerAfterRefocus_JumpsWhenAccepted()
	{
		await SignInAsync();
		OpenReader open = await OpenAsync(await ImportBookAsync());
		LoseFocus(open.Page);

		ServeRemotePosition(Chapter(4), DateTimeOffset.UtcNow.AddHours(1));
		TestDialogs.AnswerNextAlert(accept: true);
		RegainFocus(open.Page);

		await Wait.UntilAsync(() => open.ViewModel.Locator.ResourceHref == Chapter(4), "the reader to jump to the synced position", readerTimeout);
		Assert.Equal("Continue from another device?", Assert.Single(TestDialogs.Alerts).Title);

		// The same position again is not newer than the one just adopted.
		LoseFocus(open.Page);
		RegainFocus(open.Page);
		await Task.Delay(500);
		Assert.Single(TestDialogs.Alerts);
	}

	[UIFact]
	public async Task ReadingPosition_IsPushedWhenLeavingTheReader()
	{
		await SignInAsync();
		FakeNetwork.Firebase.On(request => request.Method == HttpMethod.Patch, _ => FakeServer.Text(HttpStatusCode.OK, "{}"));
		OpenReader open = await OpenAsync(await ImportBookAsync());
		await open.Reader.SetLocatorAsync(new EpubLocator(Chapter(2), 0, 1));
		await Wait.UntilAsync(() => open.ViewModel.Locator.ResourceHref == Chapter(2), "the reader to move to chapter 2", readerTimeout);

		await SendBridgeMessageAsync(open.Reader, "requestExit");
		await TestApp.WaitForPageAsync<LibraryPage>();

		await Wait.UntilAsync(
			() => FakeNetwork.Firebase.Requests.Any(request => request.Method == HttpMethod.Patch && request.Body!.Contains(Chapter(2), StringComparison.Ordinal)),
			"the position to be pushed to Firestore");
		Assert.Contains($"/positions/{open.Book.ContentHash}", FakeNetwork.Firebase.Requests.Last(request => request.Method == HttpMethod.Patch).Url);
	}
}
