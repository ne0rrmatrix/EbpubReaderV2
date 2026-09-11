using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

public partial class OpdsCatalogPage : ContentPage, IQueryAttributable
{
	readonly OpdsCatalogViewModel viewModel;
	bool initialized;

	public OpdsCatalogPage(OpdsCatalogViewModel viewModel)
	{
		this.viewModel = viewModel;
		BindingContext = viewModel;
		InitializeComponent();
		SizeChanged += OnPageSizeChanged;
	}

	internal OpdsCatalogViewModel ViewModel => viewModel;

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S2325:Make method static", Justification = "It is an event handler")]
	void OnPageSizeChanged(object? sender, EventArgs e)
	{
		double width = Width;
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
		viewModel.OnPageDisappearing();
	}

	async void OnSearchEntryCompleted(object? sender, EventArgs e)
	{
		if (viewModel.SearchCommand.CanExecute(null))
		{
			Task? task = viewModel.SearchCommand.ExecuteAsync(null);
			if (task is not null)
			{
				await task;
			}
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S3168:async methods should not return void", Justification = "It is an event handler")]
	public async void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (initialized || query is null || !query.TryGetValue("feedUrl", out object? feedUrl))
		{
			return;
		}

		if (feedUrl is not string url || string.IsNullOrWhiteSpace(url))
		{
			return;
		}

		initialized = true;
		query.TryGetValue("title", out object? title);
		// InitializeAsync never throws; load failures surface via StatusMessage.
		await viewModel.InitializeAsync(url, title as string);
	}
}