using CommunityToolkit.Mvvm.ComponentModel;

namespace DisplayBook.App.Models;

public sealed partial class LibraryBookItem(BookSummary book) : ObservableObject
{
	bool isSelected;

	public bool IsSelected
	{
		get => isSelected;
		set => SetProperty(ref isSelected, value);
	}

	public BookSummary Book { get; } = book;

	public string Id => Book.Id;

	public string Title => Book.Title;

	public string Author => Book.Author;

	public string CoverPath => Book.CoverPath;
}