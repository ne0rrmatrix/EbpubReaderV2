using CommunityToolkit.Mvvm.ComponentModel;

namespace DisplayBook.App.Models;

public sealed partial class LibraryBookItem(BookSummary book) : ObservableObject
{
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public BookSummary Book { get; } = book;

    public string Id => Book.Id;

    public string Title => Book.Title;

    public string Author => Book.Author;

    public string CoverPath => Book.CoverPath;
}
