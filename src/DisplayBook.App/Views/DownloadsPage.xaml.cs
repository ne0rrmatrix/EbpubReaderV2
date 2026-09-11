using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

/// <summary>
/// Downloads page: shows the OPDS download queue with pause/resume/cancel controls.
/// The ViewModel rebuilds its collection from the queue's snapshot on each appearance
/// so returning to the page always reflects current state.
/// </summary>
public partial class DownloadsPage : ContentPage
{
	readonly DownloadsViewModel viewModel;

	public DownloadsPage(DownloadsViewModel viewModel)
	{
		this.viewModel = viewModel;
		BindingContext = viewModel;
		InitializeComponent();
	}

	internal DownloadsViewModel ViewModel => viewModel;

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await viewModel.InitializeAsync();
	}
}
