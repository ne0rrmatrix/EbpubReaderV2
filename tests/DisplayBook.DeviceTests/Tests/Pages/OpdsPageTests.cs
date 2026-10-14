using System.Net.Sockets;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.ViewModels;
using DisplayBook.App.Views;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests.Pages;

/// <summary>Shared navigation for the OPDS page tests, all served by <see cref="FakeCalibre"/>.</summary>
public abstract class OpdsPageTest : AppDataTest
{
	public override async Task InitializeAsync()
	{
		await base.InitializeAsync();
		FakeCalibre.Serve();
	}

	protected static async Task<OpdsCatalogViewModel> OpenCatalogAsync(string feedUrl = FakeCalibre.Root, string title = "Test server")
	{
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();
		OpdsCatalogPage page = await TestApp.GoToAsync<OpdsCatalogPage>(
			$"opds/catalog?feedUrl={Uri.EscapeDataString(feedUrl)}&title={Uri.EscapeDataString(title)}");
		await Wait.UntilAsync(() => !page.ViewModel.IsLoading && page.ViewModel.CurrentUrl is not null || page.ViewModel.StatusMessage is not null, "the catalog to load");
		return page.ViewModel;
	}

	protected static async Task OpenFolderAsync(OpdsCatalogViewModel catalog, string title)
	{
		string? before = catalog.CurrentUrl;
		await catalog.Entries.Single(entry => entry.Title == title).OpenCommand.ExecuteAsync(null);
		await Wait.UntilAsync(() => !catalog.IsLoading && (catalog.CurrentUrl != before || catalog.StatusMessage is not null), $"'{title}' to load");
	}

	protected static string[] Titles(OpdsCatalogViewModel catalog) => [.. catalog.Entries.Select(entry => entry.Title)];

	protected static DownloadCenterViewModel Downloads => TestApp.GetService<DownloadCenterViewModel>();

	protected static async Task<string[]> LibraryTitlesAsync() =>
		[.. (await TestApp.GetService<IBookCatalogService>().GetBooksAsync()).Select(book => book.Title).Order()];

	/// <summary>Waits for every download to finish, checks they all succeeded, then closes the download popup.</summary>
	protected static async Task FinishDownloadsAsync(int expected)
	{
		DownloadCenterViewModel downloads = Downloads;
		await Wait.UntilAsync(
			() => downloads.Items.Count == expected && downloads.Items.All(item => item.IsFinished) && !downloads.HasActiveDownloads,
			"the downloads to finish");
		Assert.All(downloads.Items, item => Assert.True(item.Status == DownloadStatus.Completed, $"{item.BookTitle}: {item.StatusText} {item.Error}"));
		await Wait.UntilAsync(() => downloads.IsPopupVisible, "the download popup to be shown");
		downloads.DismissCommand.Execute(null);
		await Wait.UntilAsync(() => !downloads.IsPopupVisible, "the download popup to close");
	}
}

public class OpdsServersPageTests : OpdsPageTest
{
	static FakeBonjourDiscovery Discovery => (FakeBonjourDiscovery)TestApp.GetService<IBonjourDiscoveryService>();

	static async Task<OpdsServersViewModel> OpenServersAsync()
	{
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();
		OpdsServersPage page = await TestApp.GoToAsync<OpdsServersPage>("opds/servers");
		return page.ViewModel;
	}

	[UIFact]
	public async Task AddManual_InvalidUrl_ExplainsTheExpectedFormat()
	{
		OpdsServersViewModel servers = await OpenServersAsync();
		servers.ManualUrl = "calibre on my desk";

		await servers.AddManualCommand.ExecuteAsync(null);

		Assert.StartsWith("Enter a valid http(s) URL", servers.StatusMessage);
		Assert.Empty(await TestApp.GetService<IOpdsServerRepository>().GetAllAsync());
	}

	[UIFact]
	public async Task AddManual_SavesTheServerAndOpensItsCatalog()
	{
		OpdsServersViewModel servers = await OpenServersAsync();
		servers.ManualUrl = $" {FakeCalibre.Root} ";
		servers.ManualName = "Desk";

		await servers.AddManualCommand.ExecuteAsync(null);

		OpdsCatalogPage catalog = await TestApp.WaitForPageAsync<OpdsCatalogPage>();
		await Wait.UntilAsync(() => catalog.ViewModel.Entries.Count > 0, "the catalog to load");
		Assert.Equal("calibre Library", catalog.ViewModel.Title);
		OpdsServer saved = Assert.Single(await TestApp.GetService<IOpdsServerRepository>().GetAllAsync());
		Assert.Equal(("Desk", ServerType.Manual), (saved.Name, saved.Type));
		Assert.Equal(string.Empty, servers.ManualUrl);
	}

	[UIFact]
	public async Task SavedServers_CanBeDisabledOpenedAndRemoved()
	{
		IOpdsServerRepository repository = TestApp.GetService<IOpdsServerRepository>();
		await repository.AddAsync(new OpdsServer { Id = "s1", Name = "Desk", Url = FakeCalibre.Root });
		OpdsServersViewModel servers = await OpenServersAsync();
		await Wait.UntilAsync(() => servers.Saved.Count == 1, "the saved servers to load");
		OpdsServerDisplayModel desk = servers.Saved[0];
		Assert.Equal(("Desk", "SAVED", false), (desk.Name, desk.BadgeText, desk.IsDiscovered));

		desk.IsEnabled = false;
		desk.IsEnabled = true;
		desk.IsEnabled = false;
		await Wait.UntilAsync(() => repository.GetById("s1")?.IsEnabled == false, "the disabled state to be saved");
		Assert.False(desk.OpenCommand.CanExecute(null));

		desk.IsEnabled = true;
		await desk.OpenCommand.ExecuteAsync(null);
		await TestApp.WaitForPageAsync<OpdsCatalogPage>();

		// Coming back reloads the list, so take the row from the reloaded list.
		await TestApp.GoToAsync<OpdsServersPage>("..");
		await Wait.UntilAsync(() => servers.Saved.Count == 1 && !ReferenceEquals(servers.Saved[0], desk), "the saved servers to reload");
		desk = servers.Saved[0];
		await desk.RemoveCommand.ExecuteAsync(null);
		Assert.Empty(servers.Saved);
		Assert.Equal("Removed Desk.", servers.StatusMessage);
		Assert.Empty(await repository.GetAllAsync());
	}

	[UIFact]
	public async Task Discovery_ListsAndSavesServersFoundOnTheNetwork()
	{
		OpdsServersViewModel servers = await OpenServersAsync();
		await servers.ToggleDiscoveryCommand.ExecuteAsync(null);
		Assert.True(servers.IsDiscovering);
		Assert.Equal("Stop discovery", servers.ToggleButtonText);

		OpdsServer found = new() { Id = "found", Name = "Books in calibre", Url = "http://192.168.1.20:8080/opds", Type = ServerType.Discovered };
		Discovery.RaiseDiscovered(found, isNew: true);
		Discovery.RaiseDiscovered(found, isNew: false);
		await Wait.UntilAsync(() => servers.Discovered.Count == 1, "the discovered server to be listed");
		Assert.Equal("NETWORK", servers.Discovered[0].BadgeText);
		await Wait.UntilAsync(() => TestApp.GetService<IOpdsServerRepository>().FindByUrl(found.Url) is not null, "the discovered server to be saved");

		Discovery.RaiseError("mDNS scan failed.", new SocketException((int)SocketError.AccessDenied));
		await Wait.UntilAsync(() => servers.StatusMessage?.StartsWith("mDNS scan failed. (AccessDenied", StringComparison.Ordinal) == true, "the discovery error to be shown");
		Discovery.RaiseError("Discovery stalled.", new InvalidOperationException("no network"));
		await Wait.UntilAsync(() => servers.StatusMessage == "Discovery stalled. (InvalidOperationException: no network)", "the second error to be shown");
		Discovery.RaiseError("Plain message.", null);
		await Wait.UntilAsync(() => servers.StatusMessage == "Plain message.", "the plain error to be shown");

		Discovery.RaiseRemoved("http://192.168.1.20:8080", found.Name);
		await Wait.UntilAsync(() => servers.Discovered.Count == 0, "the server to drop off the list");

		await servers.ToggleDiscoveryCommand.ExecuteAsync(null);
		Assert.False(servers.IsDiscovering);
		Assert.False(Discovery.IsDiscovering);
	}

	[UIFact]
	public async Task Discovery_StartFailure_IsReported()
	{
		OpdsServersViewModel servers = await OpenServersAsync();
		FakeBonjourDiscovery.StartFailure = new InvalidOperationException("multicast is blocked");

		await servers.ToggleDiscoveryCommand.ExecuteAsync(null);

		Assert.False(servers.IsDiscovering);
		Assert.Equal("Could not start discovery: multicast is blocked", servers.StatusMessage);
	}

	[UIFact]
	public async Task LeavingThePage_StopsDiscovery()
	{
		OpdsServersViewModel servers = await OpenServersAsync();
		await servers.ToggleDiscoveryCommand.ExecuteAsync(null);

		await servers.GoBackCommand.ExecuteAsync(null);
		await TestApp.WaitForPageAsync<LibraryPage>();

		await Wait.UntilAsync(() => !Discovery.IsDiscovering, "discovery to stop");
	}

	[UIFact]
	public async Task Dispose_StopsListeningForDiscoveredServers()
	{
		OpdsServersViewModel servers = await OpenServersAsync();
		int listeners = Discovery.ServerDiscoveredListenerCount;

		servers.Dispose();
		Assert.Equal(listeners - 1, Discovery.ServerDiscoveredListenerCount);

		servers.Dispose();
		Assert.Equal(listeners - 1, Discovery.ServerDiscoveredListenerCount);
	}
}

public class OpdsCatalogPageTests : OpdsPageTest
{
	[UIFact]
	public async Task Root_ShowsFoldersAndDrillsIntoThem()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync();
		Assert.Equal("calibre Library", catalog.Title);
		Assert.Equal("Books on the test server", catalog.Subtitle);
		Assert.Equal(["By Newest", "Broken section"], Titles(catalog));
		Assert.False(catalog.HasBooks);
		Assert.Equal("Books sorted by date", catalog.Entries[0].Subtitle);

		await OpenFolderAsync(catalog, "By Newest");

		Assert.Equal(["Elric of Melniboné", "The Sailor on the Seas of Fate", "Only In Mobi"], Titles(catalog));
		Assert.True(catalog.HasBooks && catalog.CanGoBack && catalog.CanGoForward && catalog.CanSearch);
		Assert.Equal("Test server  ›  By Newest", catalog.CrumbsText);
		Assert.Equal(2, catalog.Crumbs.Count);
		CatalogEntryModel elric = catalog.Entries[0];
		Assert.Equal("Michael Moorcock", elric.Subtitle);
		Assert.True(elric.HasCover);
		Assert.Equal("http://calibre.test:8080/get/cover/1/Calibre_Library", elric.CoverUrl);
	}

	[UIFact]
	public async Task Paging_GoesForwardAndBack()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync(FakeCalibre.Newest, "Newest");

		await catalog.GoForwardCommand.ExecuteAsync(null);
		await Wait.UntilAsync(() => catalog.CurrentUrl == FakeCalibre.NewestPage2 && !catalog.IsLoading, "page 2 to load");
		Assert.Equal(["The Weird of the White Wolf"], Titles(catalog));
		Assert.False(catalog.CanGoForward);

		await catalog.GoForwardCommand.ExecuteAsync(null);
		Assert.Equal(FakeCalibre.NewestPage2, catalog.CurrentUrl);

		await catalog.GoBackCommand.ExecuteAsync(null);
		await Wait.UntilAsync(() => catalog.CurrentUrl == FakeCalibre.Newest && !catalog.IsLoading, "page 1 to load again");
		Assert.False(catalog.CanGoBack);

		await catalog.GoBackCommand.ExecuteAsync(null);
		Assert.Equal(FakeCalibre.Newest, catalog.CurrentUrl);
	}

	[UIFact]
	public async Task NavigateBack_GoesUpAFolderThenLeavesThePage()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync();
		await OpenFolderAsync(catalog, "By Newest");

		await catalog.NavigateBackCommand.ExecuteAsync(null);
		await Wait.UntilAsync(() => catalog.CurrentUrl == FakeCalibre.Root && !catalog.IsLoading, "the root to load again");

		await catalog.NavigateBackCommand.ExecuteAsync(null);
		await TestApp.WaitForPageAsync<LibraryPage>();
	}

	[UIFact]
	public async Task Search_ShowsTheResults()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync();

		await catalog.SearchCommand.ExecuteAsync(null);
		Assert.Equal(FakeCalibre.Root, catalog.CurrentUrl);

		catalog.SearchText = " dune ";
		await catalog.SearchCommand.ExecuteAsync(null);
		await Wait.UntilAsync(() => catalog.CurrentUrl == $"{FakeCalibre.Search}?q=dune" && !catalog.IsLoading, "the search results to load");

		Assert.Equal(["Dune"], Titles(catalog));
		Assert.Contains(FakeNetwork.Opds.Requests, request => request.Url == $"{FakeCalibre.Search}?q=dune");
	}

	[UIFact]
	public async Task SearchEntryCompleted_RunsTheSearch()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync();
		OpdsCatalogPage page = await TestApp.WaitForPageAsync<OpdsCatalogPage>();
		catalog.SearchText = "dune";

		page.GetVisualTreeDescendants().OfType<Entry>().First().SendCompleted();

		await Wait.UntilAsync(() => Titles(catalog).SequenceEqual(["Dune"]), "the search to run");
	}

	[UIFact]
	public async Task BrokenFeed_ExplainsWhatFailed()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync();

		await OpenFolderAsync(catalog, "Broken section");

		Assert.StartsWith("Could not load this catalog:", catalog.StatusMessage);
		Assert.Equal(FakeCalibre.Root, catalog.CurrentUrl);
	}

	[UIFact]
	public async Task ReopeningAFeed_UsesTheCache()
	{
		await OpenCatalogAsync();
		int requests = FakeNetwork.Opds.Requests.Count(request => request.Url == FakeCalibre.Root);

		await OpenCatalogAsync();

		Assert.Equal(requests, FakeNetwork.Opds.Requests.Count(request => request.Url == FakeCalibre.Root));
	}

	[UIFact]
	public async Task Selection_SelectsOnlyBooks()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync(FakeCalibre.Newest, "Newest");
		Assert.Equal("Select books to download", catalog.SelectionSummary);
		Assert.False(catalog.DownloadSelectedCommand.CanExecute(null));

		catalog.ToggleSelectionModeCommand.Execute(null);
		Assert.True(catalog.Entries.All(entry => entry.IsSelectable));
		await catalog.Entries[0].OpenCommand.ExecuteAsync(null);
		Assert.True(catalog.Entries[0].IsSelected);
		Assert.Equal("Download 1 selected", catalog.SelectionSummary);

		catalog.SelectAllCommand.Execute(null);
		Assert.Equal(3, catalog.SelectedCount);
		Assert.True(catalog.HasSelection);

		catalog.ClearSelectionCommand.Execute(null);
		Assert.Equal(0, catalog.SelectedCount);

		catalog.SelectAllCommand.Execute(null);
		catalog.ToggleSelectionModeCommand.Execute(null);
		Assert.Equal(0, catalog.SelectedCount);
		Assert.DoesNotContain(catalog.Entries, entry => entry.IsSelectable);
	}

	[UIFact]
	public async Task DownloadSelected_ImportsTheEpubsAndSkipsTheRest()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync(FakeCalibre.Newest, "Newest");
		catalog.SelectAllCommand.Execute(null);

		await catalog.DownloadSelectedCommand.ExecuteAsync(null);

		Assert.Equal("Skipped 1 book(s) not offered as EPUB.", catalog.StatusMessage);
		Assert.False(catalog.IsSelectionMode);
		await FinishDownloadsAsync(expected: 2);
		Assert.Equal(["Elric of Melniboné", "The Sailor on the Seas of Fate"], await LibraryTitlesAsync());
	}

	[UIFact]
	public async Task DownloadSelected_NoEpubs_SaysSo()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync(FakeCalibre.Newest, "Newest");
		catalog.ToggleSelectionModeCommand.Execute(null);
		catalog.Entries.Single(entry => entry.Title == "Only In Mobi").IsSelected = true;

		await catalog.DownloadSelectedCommand.ExecuteAsync(null);

		Assert.Equal("None of the selected books are offered as EPUB.", catalog.StatusMessage);
		await Wait.UntilAsync(() => Downloads.IsPopupVisible, "the download popup");
		Downloads.DismissCommand.Execute(null);
		await Wait.UntilAsync(() => !Downloads.IsPopupVisible, "the popup to close");
	}

	[UIFact]
	public async Task OpeningABook_ShowsItsDetailsPage()
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync(FakeCalibre.Newest, "Newest");
		catalog.SetServerId("server-1");

		await catalog.Entries[0].OpenCommand.ExecuteAsync(null);

		OpdsBookPage page = await TestApp.WaitForPageAsync<OpdsBookPage>();
		await Wait.UntilAsync(() => page.ViewModel.Title == "Elric of Melniboné", "the book details to load");
	}
}

public class OpdsBookPageTests : OpdsPageTest
{
	static async Task<OpdsBookViewModel> OpenBookFromCatalogAsync(string title)
	{
		OpdsCatalogViewModel catalog = await OpenCatalogAsync(FakeCalibre.Newest, "Newest");
		await catalog.Entries.Single(entry => entry.Title == title).OpenCommand.ExecuteAsync(null);
		OpdsBookPage page = await TestApp.WaitForPageAsync<OpdsBookPage>();
		await Wait.UntilAsync(() => page.ViewModel.Title == title && !page.ViewModel.IsLoading, "the book details to load");
		return page.ViewModel;
	}

	[UIFact]
	public async Task Details_ShowTheBookAndItsEpubDownload()
	{
		OpdsBookViewModel book = await OpenBookFromCatalogAsync("Elric of Melniboné");

		Assert.Equal("Michael Moorcock", book.Authors);
		Assert.Equal("The story of Elric of Melniboné.", book.Description);
		Assert.True(book.HasCover);
		DownloadLink link = Assert.Single(book.DownloadLinks);
		Assert.True(link.IsEpub);
		Assert.True(book.HasDownloadOptions);
		Assert.False(book.IsInLibrary);
	}

	[UIFact]
	public async Task Download_ImportsTheBookAndMarksItInTheLibrary()
	{
		OpdsBookViewModel book = await OpenBookFromCatalogAsync("Elric of Melniboné");
		OpdsBookPage page = await TestApp.WaitForPageAsync<OpdsBookPage>();
		int appearing = 0, disappearing = 0;
		page.Appearing += (_, _) => appearing++;
		page.Disappearing += (_, _) => disappearing++;

		Task download = book.DownloadCommand.ExecuteAsync(book.DownloadLinks[0]);
		await FinishDownloadsAsync(expected: 1);
		await download;

		string[] library = await LibraryTitlesAsync();
		await Wait.UntilAsync(() => book.IsInLibrary, $"the page to show the book as in the library (library: [{string.Join(", ", library)}], appearing {appearing}, disappearing {disappearing}, current page {Shell.Current.CurrentPage.GetType().Name})");
		Assert.False(book.HasDownloadOptions);
		Assert.Equal(["Elric of Melniboné"], await LibraryTitlesAsync());

		// Already in the library: downloading again does nothing.
		await book.DownloadCommand.ExecuteAsync(new DownloadLink { Url = FakeCalibre.Download(1), Format = "application/epub+zip" });
		Assert.Empty(Downloads.Items);
	}

	[UIFact]
	public async Task BookAlreadyInTheLibrary_OffersNoDownload()
	{
		await TestApp.GetService<IBookImportService>().ImportLocalFileAsync(TestEpub.Write("The Sailor on the Seas of Fate", author: "Michael Moorcock"));

		OpdsBookViewModel book = await OpenBookFromCatalogAsync("The Sailor on the Seas of Fate");

		Assert.True(book.IsInLibrary);
		Assert.False(book.HasDownloadOptions);
	}

	[UIFact]
	public async Task NoEpubOffered_SaysSo()
	{
		OpdsBookViewModel book = await OpenBookFromCatalogAsync("Only In Mobi");

		Assert.Empty(book.DownloadLinks);
		Assert.Equal("This book is not offered as EPUB.", book.StatusMessage);
	}

	[UIFact]
	public async Task OpenedByUrl_LoadsTheEntryDocument()
	{
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();

		OpdsBookPage page = await TestApp.GoToAsync<OpdsBookPage>($"opds/book?entryUrl={Uri.EscapeDataString(FakeCalibre.EntryDocument)}");

		await Wait.UntilAsync(() => page.ViewModel.Title == "Elric of Melniboné" && !page.ViewModel.IsLoading, "the entry document to load");
		Assert.Single(page.ViewModel.DownloadLinks);
	}

	[UIFact]
	public async Task UnreachableEntry_ExplainsWhatFailed()
	{
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();

		OpdsBookPage page = await TestApp.GoToAsync<OpdsBookPage>($"opds/book?entryUrl={Uri.EscapeDataString("http://calibre.test:8080/opds/entry/missing")}");

		await Wait.UntilAsync(() => page.ViewModel.StatusMessage is not null, "the error to be shown");
		Assert.StartsWith("Could not load this book:", page.ViewModel.StatusMessage);

		await page.ViewModel.GoBackCommand.ExecuteAsync(null);
		await TestApp.WaitForPageAsync<LibraryPage>();
		page.ViewModel.Dispose();
		page.ViewModel.Dispose();
	}
}
