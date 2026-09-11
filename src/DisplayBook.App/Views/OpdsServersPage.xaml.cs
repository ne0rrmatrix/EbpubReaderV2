using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class OpdsServersPage : ContentPage
{
	readonly OpdsServersViewModel viewModel;

	public OpdsServersPage(OpdsServersViewModel viewModel)
	{
		this.viewModel = viewModel;
		BindingContext = viewModel;
		InitializeComponent();
	}

	internal OpdsServersViewModel ViewModel => viewModel;

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await viewModel.OnPageAppearingAsync();
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		viewModel.OnPageDisappearing();
	}
}
