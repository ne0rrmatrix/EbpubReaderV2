using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class BookDetailsPage : ContentPage
{
	public BookDetailsPage(BookDetailsViewModel viewModel)
	{
		BindingContext = viewModel;
		InitializeComponent();
	}
}
