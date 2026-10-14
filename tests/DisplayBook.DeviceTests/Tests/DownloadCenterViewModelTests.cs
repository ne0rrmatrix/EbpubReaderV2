using System.Net;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.ViewModels;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests;

/// <summary>
/// The download popup's view model (the app's singleton, over the app's real download queue),
/// for the states the OPDS page tests don't reach: the preparing phase, failures, and per-row
/// cancel and retry.
/// </summary>
public class DownloadCenterViewModelTests : AppDataTest
{
	static DownloadCenterViewModel Downloads => TestApp.GetService<DownloadCenterViewModel>();

	static IDownloadQueueService Queue => TestApp.GetService<IDownloadQueueService>();

	static DownloadLink Link(int id) => new() { Url = $"http://calibre.test/get/epub/{id}", Format = "application/epub+zip" };

	[UIFact]
	public async Task Preparing_ShowsProgressThenThePublishedQueue()
	{
		DownloadCenterViewModel downloads = Downloads;

		// One BeginPreparing per EndPreparing: a second Begin before End would leave the shared
		// view model ignoring queue updates for good (EndPreparing only undoes one).
		CancellationToken token = downloads.BeginPreparing(5);
		Assert.Equal("Preparing 5 downloads…", downloads.PreparingText);
		downloads.ReportPreparing(3);
		Assert.Equal(("Preparing downloads", "Preparing 3 of 5…"), (downloads.HeaderText, downloads.SummaryText));
		Assert.True(downloads.HasActiveDownloads);
		Assert.Equal("Hide", downloads.DismissButtonText);

		downloads.EndPreparing();
		downloads.EndPreparing();
		downloads.ReportPreparing(4);
		Assert.False(downloads.IsPreparing);
		Assert.Equal(("Downloads", "Nothing is downloading right now."), (downloads.HeaderText, downloads.SummaryText));
		Assert.Equal("Done", downloads.DismissButtonText);
		Assert.False(token.IsCancellationRequested);
	}

	[UIFact]
	public async Task CancelAll_WhilePreparing_CancelsThePreparation()
	{
		DownloadCenterViewModel downloads = Downloads;
		CancellationToken token = downloads.BeginPreparing(10);

		await downloads.CancelAllCommand.ExecuteAsync(null);

		Assert.True(token.IsCancellationRequested);
		downloads.EndPreparing();
	}

	[UIFact]
	public async Task FailedDownloads_AreSummarisedAndCanBeRetried()
	{
		DownloadCenterViewModel downloads = Downloads;
		FakeNetwork.Opds.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

		await Queue.EnqueueAsync(Link(1), "First");
		await Wait.UntilAsync(() => downloads.Items.Count == 1 && downloads.Items[0].Status == DownloadStatus.Failed, "the download to fail");
		await Wait.UntilAsync(() => downloads.HeaderText == "Download failed", "the header to update");
		DownloadItemModel first = downloads.Items[0];
		Assert.Contains("1 failed or canceled", downloads.SummaryText);
		Assert.True(first.CanRetry);
		Assert.Equal("Failed", first.StatusText);
		Assert.StartsWith("Download failed: server responded 503", first.Error);

		await Queue.EnqueueAsync(Link(2), "Second");
		await Wait.UntilAsync(() => downloads.Items.Count == 2 && downloads.HeaderText == "Downloads failed", "both downloads to fail");

		FakeNetwork.Opds.On(_ => true, _ => FakeServer.Bytes(TestEpub.Build("First")));
		await first.RetryCommand.ExecuteAsync(null);
		await Wait.UntilAsync(() => first.Status == DownloadStatus.Completed, "the retried download to finish");
		Assert.True(first.IsFinished);

		await downloads.ClearFinishedCommand.ExecuteAsync(null);
		Assert.Empty(downloads.Items);
	}

	[UIFact]
	public async Task CancellingARow_StopsThatDownload()
	{
		DownloadCenterViewModel downloads = Downloads;
		TaskCompletionSource release = new();
		FakeNetwork.Opds.On(_ => true, async _ =>
		{
			await release.Task;
			return FakeServer.Bytes(TestEpub.Build("Slow"));
		});

		await Queue.EnqueueAsync(Link(1), "Slow");
		await Wait.UntilAsync(() => downloads.Items.Count == 1 && downloads.Items[0].Status == DownloadStatus.Downloading, "the download to start");
		DownloadItemModel row = downloads.Items[0];
		Assert.True(row.IsCancellable);

		await row.CancelCommand.ExecuteAsync(null);
		release.SetResult();

		await Wait.UntilAsync(() => row.Status == DownloadStatus.Canceled, "the row to show as cancelled");
		Assert.Equal("Canceled", row.StatusText);
	}

	[UIFact]
	public async Task Dispose_StopsListeningToTheQueue()
	{
		DownloadCenterViewModel downloads = Downloads;
		DownloadCenterViewModel separate = ActivatorUtilities.CreateInstance<DownloadCenterViewModel>(TestApp.Services);
		FakeNetwork.Opds.On(_ => true, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

		separate.Dispose();
		separate.Dispose();
		await Queue.EnqueueAsync(Link(1), "First");

		// The app's own view model is still subscribed, so once it has the update the
		// disposed one would have too.
		await Wait.UntilAsync(() => downloads.Items.Count == 1 && downloads.Items[0].Status == DownloadStatus.Failed, "the download to fail");
		Assert.Empty(separate.Items);

		// The sandbox reset clears the queue but not this shared view model's rows.
		await downloads.ClearFinishedCommand.ExecuteAsync(null);
	}
}
