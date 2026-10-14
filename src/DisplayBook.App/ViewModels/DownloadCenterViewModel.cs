using System.Collections.ObjectModel;
using CommunityToolkit.Maui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Views;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Backs <see cref="DownloadProgressPopup"/>: a live view of the OPDS download queue
/// with an overall progress summary and per-item cancel/retry. Registered as a singleton
/// so the popup can be dismissed and reopened (or opened from a different page) without
/// losing the rows for downloads that are still running.
/// </summary>
/// <remarks>
/// Queuing a large selection is a two-phase job: the caller opens the popup and enters the
/// "preparing" phase first, then does the slow work on a background thread. Row updates are
/// suspended for the duration and the whole collection is swapped in once at the end, because
/// adding hundreds of rows one notification at a time locks up the UI thread.
/// </remarks>
public sealed partial class DownloadCenterViewModel : ObservableObject, IDisposable
{
	/// <summary>Approximate rendered height of one download row, used to size the scroll area.</summary>
	const double rowHeight = 104;

	const double maxItemsAreaHeight = 312;

	readonly IDownloadQueueService queue;
	readonly IPopupService popups;
	readonly ILogger<DownloadCenterViewModel> logger;
	readonly Dictionary<string, DownloadItemModel> byId = [];
	CancellationTokenSource? prepareCts;
	int suspendDepth;
	int prepareTotal;
	bool recalculatePending;
	bool disposed;

	public DownloadCenterViewModel(
		IDownloadQueueService queue,
		IPopupService popups,
		ILogger<DownloadCenterViewModel> logger)
	{
		this.queue = queue;
		this.popups = popups;
		this.logger = logger;
		this.queue.ItemUpdated += OnItemUpdated;
	}

	/// <summary>Raised when the view model wants the popup hosting it to close.</summary>
	public event EventHandler? CloseRequested;

	/// <summary>
	/// Replaced wholesale rather than mutated in bulk: one binding change costs a single
	/// layout pass, where hundreds of individual Add notifications cost hundreds.
	/// </summary>
	[ObservableProperty]
	public partial ObservableCollection<DownloadItemModel> Items { get; set; } = [];

	/// <summary>True while the popup is on screen, so a second download does not stack another one.</summary>
	public bool IsPopupVisible { get; private set; }

	[ObservableProperty]
	public partial string HeaderText { get; set; } = "Downloads";

	[ObservableProperty]
	public partial string SummaryText { get; set; } = string.Empty;

	[ObservableProperty]
	public partial double OverallProgress { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(DismissButtonText))]
	public partial bool HasActiveDownloads { get; set; }

	[ObservableProperty]
	public partial bool HasFinishedDownloads { get; set; }

	[ObservableProperty]
	public partial bool IsPreparing { get; set; }

	[ObservableProperty]
	public partial string PreparingText { get; set; } = string.Empty;

	[ObservableProperty]
	public partial bool HasItems { get; set; }

	/// <summary>Height given to the download list so it scrolls instead of growing without bound.</summary>
	[ObservableProperty]
	public partial double ItemsAreaHeight { get; set; }

	[ObservableProperty]
	public partial string? StatusMessage { get; set; }

	public string DismissButtonText => HasActiveDownloads ? "Hide" : "Done";

	/// <summary>
	/// Enters the preparing phase and returns the token that cancels it. Call this before
	/// <see cref="ShowAsync"/> so the popup paints the "preparing" state immediately, then
	/// do the queuing work in the background and finish with <see cref="EndPreparing"/>.
	/// </summary>
	public CancellationToken BeginPreparing(int total)
	{
		prepareCts?.Dispose();
		prepareCts = new CancellationTokenSource();
		prepareTotal = total;
		PreparingText = total == 1 ? "Preparing 1 download…" : $"Preparing {total} downloads…";
		IsPreparing = true;
		suspendDepth++;
		StatusMessage = null;
		Recalculate();
		return prepareCts.Token;
	}

	/// <summary>Updates the preparing caption. Must be called on the UI thread.</summary>
	public void ReportPreparing(int prepared)
	{
		if (IsPreparing)
		{
			PreparingText = $"Preparing {Math.Min(prepared, prepareTotal)} of {prepareTotal}…";
			Recalculate();
		}
	}

	/// <summary>Leaves the preparing phase and publishes everything the queue accepted.</summary>
	public void EndPreparing()
	{
		if (!IsPreparing)
		{
			return;
		}

		IsPreparing = false;
		suspendDepth = Math.Max(0, suspendDepth - 1);
		RefreshFromQueue();
	}

	/// <summary>
	/// Shows the download popup over the current page, awaiting its dismissal. Finished rows
	/// are dropped once the popup closes with nothing left running, so the next download
	/// starts from a clean list.
	/// </summary>
	public async Task ShowAsync()
	{
		if (IsPopupVisible)
		{
			return;
		}

		IsPopupVisible = true;
		try
		{
			RefreshFromQueue();

			// Shape is cleared so the popup's own Border draws the whole card: the default
			// shape paints a light background that reads badly behind the dark theme.
			await popups.ShowPopupAsync<DownloadProgressPopup>(
				Shell.Current,
				new PopupOptions { CanBeDismissedByTappingOutsideOfPopup = true, Shape = null },
				shellParameters: null,
				CancellationToken.None);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not show the download popup.");
		}
		finally
		{
			IsPopupVisible = false;
			if (!HasActiveDownloads)
			{
				await ClearFinishedAsync();
			}
		}
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;
		queue.ItemUpdated -= OnItemUpdated;
		prepareCts?.Dispose();
	}

	internal Task CancelAsync(string itemId) => GuardAsync(() => queue.CancelAsync(itemId, CancellationToken.None));

	internal Task RetryAsync(string itemId) => GuardAsync(() => queue.ResumeAsync(itemId, CancellationToken.None));

	void OnItemUpdated(object? sender, DownloadItemEventArgs e)
	{
		if (Volatile.Read(ref suspendDepth) > 0)
		{
			// A bulk operation is running; it publishes the whole queue when it finishes.
			return;
		}

		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (!disposed)
			{
				UpdateItem(e.Progress);
			}
		});
	}

	void UpdateItem(DownloadProgress progress)
	{
		if (byId.TryGetValue(progress.Id, out DownloadItemModel? model))
		{
			model.Apply(progress);
		}
		else
		{
			model = new DownloadItemModel(progress, this);
			byId[progress.Id] = model;
			Items.Add(model);
		}

		ScheduleRecalculate();
	}

	/// <summary>
	/// Collapses a burst of item updates into one recalculation. Applying an update is O(1) but
	/// <see cref="Recalculate"/> walks every row, so doing it per event costs O(n²) across a
	/// large batch. The posted callback always runs after the last update in the burst, so the
	/// settled totals are never left stale.
	/// </summary>
	void ScheduleRecalculate()
	{
		if (recalculatePending)
		{
			return;
		}

		recalculatePending = true;
		MainThread.BeginInvokeOnMainThread(() =>
		{
			recalculatePending = false;
			if (!disposed)
			{
				Recalculate();
			}
		});
	}

	/// <summary>Rebuilds the whole list from the queue in one binding update.</summary>
	void RefreshFromQueue()
	{
		ObservableCollection<DownloadItemModel> refreshed = [];
		byId.Clear();
		foreach (DownloadProgress progress in queue.GetItems().OrderBy(item => item.StartedAt))
		{
			DownloadItemModel model = new(progress, this);
			byId[progress.Id] = model;
			refreshed.Add(model);
		}

		Items = refreshed;
		Recalculate();
	}

	void Recalculate()
	{
		// One pass rather than a LINQ chain per figure: this runs on the UI thread for every
		// progress tick, against a list that can be thousands of rows long.
		int total = Items.Count;
		int completed = 0;
		int failed = 0;
		double progress = 0;
		foreach (DownloadItemModel item in Items)
		{
			if (item.Status == DownloadStatus.Completed)
			{
				completed++;
				progress += 1d;
			}
			else
			{
				if (item.Status is DownloadStatus.Failed or DownloadStatus.Canceled)
				{
					failed++;
				}

				progress += item.Percentage;
			}
		}

		int active = total - completed - failed;

		HasActiveDownloads = IsPreparing || active > 0;
		HasFinishedDownloads = completed + failed > 0;
		HasItems = total > 0;
		ItemsAreaHeight = Math.Min(total * rowHeight, maxItemsAreaHeight);
		OverallProgress = total == 0 ? 0 : progress / total;
		HeaderText = IsPreparing ? "Preparing downloads" : BuildHeaderText(total, completed, failed, active);
		SummaryText = IsPreparing ? PreparingText : BuildSummaryText(total, completed, failed, active);
	}

	static string BuildHeaderText(int total, int completed, int failed, int active)
	{
		if (total == 0)
		{
			return "Downloads";
		}

		if (active > 0)
		{
			return total == 1 ? "Downloading" : $"Downloading {completed + failed + 1} of {total}";
		}

		if (failed == total)
		{
			return total == 1 ? "Download failed" : "Downloads failed";
		}

		return total == 1 ? "Download complete" : "Downloads complete";
	}

	static string BuildSummaryText(int total, int completed, int failed, int active)
	{
		if (total == 0)
		{
			return "Nothing is downloading right now.";
		}

		List<string> parts = [$"{completed} of {total} complete"];
		if (active > 0)
		{
			parts.Add($"{active} pending");
		}

		if (failed > 0)
		{
			parts.Add($"{failed} failed or canceled");
		}

		return string.Join("  ·  ", parts);
	}

	async Task GuardAsync(Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Download queue operation failed.");
			StatusMessage = ex.Message;
		}
	}

	[RelayCommand]
	void Dismiss() => CloseRequested?.Invoke(this, EventArgs.Empty);

	/// <summary>
	/// Stops everything: the preparing phase first (so no further books are queued), then every
	/// download the queue still has in flight. Cancels against the queue in one call rather than
	/// row by row, because during preparing the rows have not been published yet and because a
	/// batch can be thousands of books.
	/// </summary>
	[RelayCommand]
	async Task CancelAllAsync()
	{
		if (prepareCts is { } cts)
		{
			await cts.CancelAsync();
		}

		Interlocked.Increment(ref suspendDepth);
		try
		{
			await GuardAsync(() => queue.CancelAllAsync(CancellationToken.None));
		}
		finally
		{
			Interlocked.Decrement(ref suspendDepth);
			RefreshFromQueue();
		}
	}

	[RelayCommand]
	async Task ClearFinishedAsync()
	{
		await GuardAsync(() => queue.ClearFinishedAsync(CancellationToken.None));
		RefreshFromQueue();
	}
}
