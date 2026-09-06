using DisplayBook.App.Models;
using DisplayBook.App.ViewModels;
using DisplayBook.App.Views;

namespace DisplayBook.App.Services;

public sealed class NavigationService(
    IServiceProvider serviceProvider,
    IBookCatalogService catalogService) : INavigationService
{
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

    private static INavigation GetNavigation()
    {
        return Shell.Current?.Navigation ?? throw new InvalidOperationException("The application window is not ready.");
    }
}
