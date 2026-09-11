using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class LibraryPage : ContentPage
{
	readonly LibraryViewModel viewModel;

	public LibraryPage(LibraryViewModel viewModel)
	{
		this.viewModel = viewModel;
		BindingContext = viewModel;
		InitializeComponent();
		SizeChanged += OnPageSizeChanged;
	}

	internal LibraryViewModel ViewModel => viewModel;

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await viewModel.ReloadCatalogAsync(CancellationToken.None);
	}

	void OnPageSizeChanged(object? sender, EventArgs e)
	{
		double width = Width;
		if (width <= 0)
		{
			return;
		}

		if (BooksCollection.ItemsLayout is GridItemsLayout gridLayout)
		{
			gridLayout.Span = ResponsiveGridSpan.Compute(width);
		}
	}
}