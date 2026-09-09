using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class OpdsCatalogPage : ContentPage, IQueryAttributable
{
    private readonly OpdsCatalogViewModel _viewModel;
    private bool _initialized;
    private Task? _initialization;

    public OpdsCatalogPage(OpdsCatalogViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
        SizeChanged += OnPageSizeChanged;
    }

    internal OpdsCatalogViewModel ViewModel => _viewModel;

    private void OnPageSizeChanged(object? sender, EventArgs e)
    {
        var width = Width;
        if (width <= 0)
        {
            return;
        }

        if (EntriesCollection.ItemsLayout is GridItemsLayout gridLayout)
        {
            gridLayout.Span = ResponsiveGridSpan.Compute(width);
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.OnPageDisappearing();
    }

    async void OnSearchEntryCompleted(object? sender, EventArgs e)
    {
        if (_viewModel.SearchCommand.CanExecute(null))
        {
            var task = _viewModel.SearchCommand.ExecuteAsync(null);
            if (task is not null)
            {
                await task;
            }
        }
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (_initialized || query is null || !query.TryGetValue("feedUrl", out var feedUrl))
        {
            return;
        }

        if (feedUrl is not string url || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        _initialized = true;
        query.TryGetValue("title", out var title);
        // InitializeAsync never throws; load failures surface via StatusMessage.
        _initialization = _viewModel.InitializeAsync(url, title as string);
    }
}
