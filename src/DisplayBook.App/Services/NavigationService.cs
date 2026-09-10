using DisplayBook.App.Models;
using DisplayBook.App.ViewModels;
using DisplayBook.App.Views;

namespace DisplayBook.App.Services;

public sealed class NavigationService(
    IServiceProvider serviceProvider,
    IBookCatalogService catalogService) : INavigationService
{
    /// <summary>
    /// Catalog entry most recently tapped to open a book, keyed by the entry detail URL.
    /// Lets the book page fall back to entry metadata when the URL has no parseable detail document
    /// (Calibre content servers expose only download links).
    /// </summary>
    private readonly Dictionary<string, OpdsEntry> _pendingEntries = new(StringComparer.OrdinalIgnoreCase);

    public async Task ShowBookDetailsAsync(BookSummary book)
    {
        var page = serviceProvider.GetRequiredService<BookDetailsPage>();
        ((BookDetailsViewModel)page.BindingContext).SetBook(book);
        await GetNavigation().PushAsync(page);
    }

    public async Task ShowReaderAsync(BookSummary book)
    {
        var currentBook = (await catalogService.GetBooksAsync())
            .FirstOrDefault(candidate => string.Equals(candidate.Id, book.Id, StringComparison.Ordinal)) ?? book;
        var page = serviceProvider.GetRequiredService<ReaderPage>();
        ((ReaderViewModel)page.BindingContext).SetBook(currentBook);
        await GetNavigation().PushAsync(page);
    }

    public Task GoBackAsync()
    {
        return GetNavigation().PopAsync();
    }

    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        var shell = Shell.Current ?? throw new InvalidOperationException("The application window is not ready.");
        return shell.DisplayAlertAsync(title, message, accept, cancel);
    }

    public Task OpenExternalLinkAsync(Uri uri) => Launcher.Default.OpenAsync(uri);

    public Task ShowOpdsServersAsync()
        => GetShell().GoToAsync("opds/servers");

    public Task ShowOpdsCatalogAsync(string feedUrl, string? title = null)
    {
        var query = $"feedUrl={Uri.EscapeDataString(feedUrl)}";
        if (!string.IsNullOrWhiteSpace(title))
        {
            query += $"&title={Uri.EscapeDataString(title)}";
        }

        return GetShell().GoToAsync($"opds/catalog?{query}");
    }

    public Task ShowOpdsBookAsync(string entryUrl, string? serverId = null, OpdsEntry? entry = null)
    {
        if (entry is not null && !string.IsNullOrWhiteSpace(entryUrl))
        {
            _pendingEntries[entryUrl] = entry;
        }

        var query = $"entryUrl={Uri.EscapeDataString(entryUrl)}";
        if (!string.IsNullOrWhiteSpace(serverId))
        {
            query += $"&serverId={Uri.EscapeDataString(serverId)}";
        }

        return GetShell().GoToAsync($"opds/book?{query}");
    }

    /// <summary>
    /// Removes and returns the catalog entry previously staged for the given URL, if any.
    /// </summary>
    public OpdsEntry? TakePendingEntry(string entryUrl)
    {
        return !string.IsNullOrWhiteSpace(entryUrl) && _pendingEntries.Remove(entryUrl, out var entry)
            ? entry
            : null;
    }

    public Task ShowDownloadsAsync()
        => GetShell().GoToAsync("opds/downloads");

    private static Shell GetShell()
    {
        return Shell.Current ?? throw new InvalidOperationException("The application window is not ready.");
    }

    private static INavigation GetNavigation()
    {
        return Shell.Current?.Navigation ?? throw new InvalidOperationException("The application window is not ready.");
    }
}
