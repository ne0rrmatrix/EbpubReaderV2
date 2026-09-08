using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class OpdsServersPage : ContentPage
{
    private readonly OpdsServersViewModel _viewModel;

    public OpdsServersPage(OpdsServersViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    internal OpdsServersViewModel ViewModel => _viewModel;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.OnPageAppearingAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.OnPageDisappearing();
    }
}
