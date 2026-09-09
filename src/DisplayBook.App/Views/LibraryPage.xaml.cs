using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class LibraryPage : ContentPage
{
    private readonly LibraryViewModel _viewModel;

    public LibraryPage(LibraryViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
    }

    internal LibraryViewModel ViewModel => _viewModel;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.ReloadCatalogAsync(CancellationToken.None);
    }

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        var width = Width;
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
