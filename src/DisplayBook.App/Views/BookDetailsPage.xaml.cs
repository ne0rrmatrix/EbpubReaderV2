using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class BookDetailsPage : ContentPage, IQueryAttributable
{
	readonly BookDetailsViewModel viewModel;

	public BookDetailsPage(BookDetailsViewModel viewModel)
	{
		this.viewModel = viewModel;
		BindingContext = viewModel;
		InitializeComponent();
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "S3168:Async methods should not return void", Justification = "This method is part of interface contract.")]
	public async void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (query.TryGetValue("id", out object? id) && id is string bookId && !string.IsNullOrWhiteSpace(bookId))
		{
			await viewModel.LoadBookAsync(bookId);
		}
	}
}