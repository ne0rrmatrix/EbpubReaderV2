using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class OpdsBookPage : ContentPage, IQueryAttributable
{
    private readonly OpdsBookViewModel _viewModel;
    private bool _initialized;
    private Task? _initialization;

    public OpdsBookPage(OpdsBookViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    internal OpdsBookViewModel ViewModel => _viewModel;

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.OnPageDisappearing();
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (_initialized || query is null || !query.TryGetValue("entryUrl", out var entryUrl))
        {
            return;
        }

        if (entryUrl is not string url || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        _initialized = true;
        // InitializeAsync never throws; load failures surface via StatusMessage.
        _initialization = _viewModel.InitializeAsync(url);
    }
}
