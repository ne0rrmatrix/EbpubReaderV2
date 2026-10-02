using CommunityToolkit.Maui.Views;
using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

/// <summary>
/// Popup shown over whichever page started an OPDS download: overall progress, one row per
/// book with its own cancel/retry, and a dismiss button. The view model is the shared
/// singleton <see cref="DownloadCenterViewModel"/>, so dismissing this popup leaves the
/// downloads running and reopening it picks the queue back up.
/// </summary>
public partial class DownloadProgressPopup : Popup
{
	public DownloadProgressPopup(DownloadCenterViewModel viewModel)
	{
		ArgumentNullException.ThrowIfNull(viewModel);
		BindingContext = viewModel;
		InitializeComponent();
		viewModel.CloseRequested += OnCloseRequested;
		Closed += (_, _) => viewModel.CloseRequested -= OnCloseRequested;
	}

	async void OnCloseRequested(object? sender, EventArgs e)
	{
		try
		{
			await CloseAsync();
		}
		catch (InvalidOperationException)
		{
			// The popup was already dismissed (e.g. by a tap outside) before the command ran.
		}
	}
}
