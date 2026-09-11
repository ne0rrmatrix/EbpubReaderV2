using DisplayBook.App.Models;

namespace DisplayBook.App.Interfaces;

public interface INavigationService
{
	Task ShowBookDetailsAsync(BookSummary book);
	Task ShowReaderAsync(BookSummary book);
	Task GoBackAsync();
	Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);

	/// <summary>Opens a URL in the system browser (e.g. a metadata-source attribution link).</summary>
	Task OpenExternalLinkAsync(Uri uri);

	/// <summary>Opens the OPDS servers page (manual entry, discovered + saved servers).</summary>
	Task ShowOpdsServersAsync();

	/// <summary>Opens an OPDS catalog feed by its feed URL.</summary>
	Task ShowOpdsCatalogAsync(string feedUrl, string? title = null);

	/// <summary>Opens an OPDS book detail page by its self/entry URL, optionally staging the catalog entry.</summary>
	Task ShowOpdsBookAsync(string entryUrl, string? serverId = null, OpdsEntry? entry = null);

	/// <summary>Removes and returns the catalog entry previously staged for the given URL, if any.</summary>
	OpdsEntry? TakePendingEntry(string entryUrl);

	/// <summary>Opens the downloads page showing queued and active downloads.</summary>
	Task ShowDownloadsAsync();

	/// <summary>Opens the settings page (sync sign-in/sign-out).</summary>
	Task ShowSettingsAsync();
}
