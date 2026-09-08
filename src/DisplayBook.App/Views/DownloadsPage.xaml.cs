using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

/// <summary>
/// Downloads page: shows the OPDS download queue with pause/resume/cancel controls.
/// The ViewModel rebuilds its collection from the queue's snapshot on each appearance
/// so returning to the page always reflects current state.
/// </summary>
public partial class DownloadsPage : ContentPage
{
    private readonly DownloadsViewModel _viewModel;

    public DownloadsPage(DownloadsViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    internal DownloadsViewModel ViewModel => _viewModel;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }
}
