using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class OpdsBookPage : ContentPage, IQueryAttributable
{
	readonly OpdsBookViewModel viewModel;
	bool initialized;
	Task? initialization;

	public OpdsBookPage(OpdsBookViewModel viewModel)
	{
		this.viewModel = viewModel;
		BindingContext = viewModel;
		InitializeComponent();
	}

	internal OpdsBookViewModel ViewModel => viewModel;

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
		viewModel.OnPageDisappearing();
	}

	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (initialized || query is null || !query.TryGetValue("entryUrl", out object? entryUrl))
		{
			return;
		}

		if (entryUrl is not string url || string.IsNullOrWhiteSpace(url))
		{
			return;
		}

		initialized = true;
		// InitializeAsync never throws; load failures surface via StatusMessage.
		initialization = viewModel.InitializeAsync(url);
	}
}
