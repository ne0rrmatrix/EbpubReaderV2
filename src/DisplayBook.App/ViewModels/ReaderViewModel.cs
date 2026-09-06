using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.Viewer.Models;

namespace DisplayBook.App.ViewModels;

public partial class ReaderViewModel(
    INavigationService navigationService,
    IBookCatalogService catalogService) : ObservableObject
{
    [ObservableProperty]
    public partial BookSummary? Book { get; set; }

    [ObservableProperty]
    public partial EpubLocator Locator { get; set; } = EpubLocator.Empty;

    public void SetBook(BookSummary book)
    {
        Book = book;
        Locator = string.IsNullOrWhiteSpace(book.LocatorResourceHref)
            ? EpubLocator.Empty
            : new EpubLocator(book.LocatorResourceHref, book.LocatorPage, book.LocatorPageCount);
    }

    [RelayCommand]
    private async Task ExitReaderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await navigationService.GoBackAsync();
    }

    public async Task UpdateLocatorAsync(EpubLocator locator)
    {
        Locator = locator;
        if (Book is null || string.IsNullOrWhiteSpace(locator.ResourceHref))
        {
            return;
        }

        await catalogService.SaveLocatorAsync(Book.Id, locator.ResourceHref, locator.Page, locator.PageCount);
    }
}
