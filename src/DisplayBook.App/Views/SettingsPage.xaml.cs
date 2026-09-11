using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class SettingsPage : ContentPage
{
	readonly SettingsViewModel viewModel;

	public SettingsPage(SettingsViewModel viewModel)
	{
		this.viewModel = viewModel;
		BindingContext = viewModel;
		InitializeComponent();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await viewModel.OnPageAppearingAsync();
	}
}