using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;

namespace DisplayBook.App.ViewModels;

public partial class BookDetailsViewModel(INavigationService navigationService) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial BookSummary? Book { get; set; }

    public void SetBook(BookSummary book)
    {
        Book = book;
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync()
    {
        return navigationService.ShowReaderAsync(Book ?? throw new InvalidOperationException("A book must be selected before opening the reader."));
    }

    [RelayCommand]
    private Task BackAsync()
    {
        return navigationService.GoBackAsync();
    }

    private bool CanOpen() => Book is not null;
}
