using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public interface INavigationService
{
    Task ShowBookDetailsAsync(BookSummary book);
    Task ShowReaderAsync(BookSummary book);
    Task GoBackAsync();
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel);
}
