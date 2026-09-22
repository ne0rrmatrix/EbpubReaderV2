using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Opds;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the OPDS catalog page: renders one feed at a time with breadcrumbs,
/// pagination, optional search, and drills down into sub-catalogs or book details.
/// Books can also be picked one at a time, in bulk, or all at once and queued for
/// download straight from the feed, which reports progress in the download popup.
/// </summary>
public sealed partial class OpdsCatalogViewModel(
	IOpdsParserService parser,
	IOpdsCatalogCache cache,
	IOpdsEntryStagingCache entryStaging,
	IDownloadQueueService queue,
	DownloadCenterViewModel downloads,
	ILogger<OpdsCatalogViewModel> logger) : ObservableObject, IDisposable
{
	readonly IOpdsParserService parser = parser;
	readonly IOpdsCatalogCache cache = cache;
	readonly IOpdsEntryStagingCache entryStaging = entryStaging;
	readonly IDownloadQueueService queue = queue;
	readonly DownloadCenterViewModel downloads = downloads;
	readonly ILogger<OpdsCatalogViewModel> logger = logger;
	readonly List<Crumb> crumbs = [];
	string? serverId;
	string? currentUrl;
	CancellationTokenSource? loadCts;
	bool disposed;

	public ObservableCollection<CatalogEntryModel> Entries { get; } = [];

	public IReadOnlyList<Crumb> Crumbs => crumbs;

	/// <summary>
	/// Breadcrumb trail rendered as a single line, e.g. "Root › Fiction › Sci-Fi".
	/// </summary>
	public string CrumbsText => string.Join("  ›  ", crumbs.Select(c => c.Title));

	[ObservableProperty]
	public partial string Title { get; set; } = "Catalog";

	[ObservableProperty]
	public partial string? Subtitle { get; set; }

	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	[ObservableProperty]
	public partial string? StatusMessage { get; set; }

	[ObservableProperty]
	public partial bool CanGoBack { get; set; }

	[ObservableProperty]
	public partial bool CanGoForward { get; set; }

	[ObservableProperty]
	public partial bool CanSearch { get; set; }

	[ObservableProperty]
	public partial string SearchText { get; set; } = string.Empty;

	/// <summary>True when the current feed holds at least one book, i.e. there is something to select.</summary>
	[ObservableProperty]
	public partial bool HasBooks { get; set; }

	[ObservableProperty]
	public partial bool IsSelectionMode { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SelectionSummary))]
	[NotifyPropertyChangedFor(nameof(HasSelection))]
	[NotifyCanExecuteChangedFor(nameof(DownloadSelectedCommand))]
	public partial int SelectedCount { get; set; }

	public bool HasSelection => SelectedCount > 0;

	public string SelectionSummary => SelectedCount == 0
		? "Select books to download"
		: $"Download {SelectedCount} selected";

	public sealed record Crumb(string Title, string Url);

	public Task InitializeAsync(string feedUrl, string? title = null)
	{
		disposed = false;
		Title = string.IsNullOrWhiteSpace(title) ? "Catalog" : title;
		crumbs.Clear();
		crumbs.Add(new Crumb(Title, feedUrl));
		return LoadFeedAsync(feedUrl, pushCrum: false);
	}

	public void OnPageDisappearing() => loadCts?.Cancel();

	internal async Task OpenEntryAsync(CatalogEntryModel model)
	{
		if (IsSelectionMode && model.IsBook)
		{
			model.IsSelected = !model.IsSelected;
			return;
		}

		string? href = GetEntryHref(model.Entry);
		if (!string.IsNullOrEmpty(href))
		{
			if (model.IsBook)
			{
				entryStaging.Stage(href, model.Entry);
				string query = $"entryUrl={Uri.EscapeDataString(href)}";
				if (!string.IsNullOrWhiteSpace(serverId))
				{
					query += $"&serverId={Uri.EscapeDataString(serverId)}";
				}

				await Shell.Current.GoToAsync($"opds/book?{query}");
			}
			else
			{
				await LoadFeedAsync(href, pushCrum: true);
			}
		}
	}

	/// <summary>Called by a card whenever its checkbox changes, to keep the selection count live.</summary>
	internal void OnEntrySelectionChanged()
		=> SelectedCount = Entries.Count(entry => entry.IsSelected);

	[RelayCommand]
	void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

	[RelayCommand]
	void SelectAll()
	{
		IsSelectionMode = true;
		foreach (CatalogEntryModel entry in Entries.Where(entry => entry.IsBook))
		{
			entry.IsSelected = true;
		}
	}

	[RelayCommand]
	void ClearSelection()
	{
		foreach (CatalogEntryModel entry in Entries)
		{
			entry.IsSelected = false;
		}
	}

	[RelayCommand(CanExecute = nameof(HasSelection))]
	Task DownloadSelectedAsync()
		=> DownloadAsync([.. Entries.Where(entry => entry.IsSelected)]);

	/// <summary>
	/// Queues the EPUB acquisition link of every given entry and opens the download popup.
	/// Entries the server offers in no readable format are skipped and reported rather than
	/// queued, since the importer only understands EPUB.
	/// </summary>
	/// <remarks>
	/// The popup is shown before any work happens and the work itself runs off the UI thread:
	/// picking links and handing hundreds of books to the queue takes long enough to look like
	/// a hang otherwise. Cancel all in the popup stops it part way through.
	/// </remarks>
	async Task DownloadAsync(IReadOnlyList<CatalogEntryModel> models)
	{
		if (models.Count == 0)
		{
			StatusMessage = "Select at least one book to download.";
			return;
		}

		// Snapshot off the bound models first: nothing after this point runs on the UI thread.
		(string Title, OpdsEntry Entry)[] snapshot = [.. models.Select(model => (model.Title, model.Entry))];
		IsSelectionMode = false;
		StatusMessage = null;

		CancellationToken token = downloads.BeginPreparing(snapshot.Length);
		Progress<int> prepared = new(downloads.ReportPreparing);

		// Deliberately not awaited: this task only completes when the popup is dismissed,
		// and the whole point is that it paints before the queuing starts.
		_ = downloads.ShowAsync();

		try
		{
			(int queued, int skipped) = await Task.Run(() => PrepareAndEnqueueAsync(snapshot, prepared, token), token);
			if (queued == 0)
			{
				StatusMessage = "None of the selected books are offered as EPUB.";
			}
			else if (skipped > 0)
			{
				StatusMessage = $"Skipped {skipped} book(s) not offered as EPUB.";
			}
			else
			{
				StatusMessage = null;
			}
		}
		catch (OperationCanceledException)
		{
			StatusMessage = "Downloads canceled.";
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not queue {Count} OPDS downloads.", snapshot.Length);
			StatusMessage = "Could not start the downloads: " + ex.Message;
		}
		finally
		{
			downloads.EndPreparing();
		}
	}

	async Task<(int Queued, int Skipped)> PrepareAndEnqueueAsync(
		(string Title, OpdsEntry Entry)[] snapshot,
		IProgress<int> prepared,
		CancellationToken token)
	{
		List<(string BookTitle, DownloadLink Link)> jobs = [];
		int skipped = 0;
		for (int index = 0; index < snapshot.Length; index++)
		{
			token.ThrowIfCancellationRequested();
			DownloadLink? link = PickEpubLink(snapshot[index].Entry);
			if (link is null)
			{
				skipped++;
			}
			else
			{
				jobs.Add((snapshot[index].Title, link));
			}

			// Reported in chunks: one UI post per book would undo the point of this thread hop.
			if ((index + 1) % 10 == 0)
			{
				prepared.Report(index + 1);
			}
		}

		prepared.Report(snapshot.Length);
		if (jobs.Count > 0)
		{
			DownloadBatchProgress batch = await queue.EnqueueBatchAsync(jobs, server: null, token);
			if (token.IsCancellationRequested)
			{
				// Cancel all swept the queue while this batch was still being handed over,
				// so it would have missed these items entirely.
				await queue.CancelBatchAsync(batch.BatchId, CancellationToken.None);
				throw new OperationCanceledException(token);
			}
		}

		return (jobs.Count, skipped);
	}

	static DownloadLink? PickEpubLink(OpdsEntry entry)
	{
		// BuildDetailsFromEntry is network-free: the acquisition links are already in the feed.
		List<DownloadLink> links = OpdsParserService.BuildDetailsFromEntry(entry).DownloadLinks;
		return links.FirstOrDefault(link =>
			link.FormatName.Equals("EPUB", StringComparison.OrdinalIgnoreCase));
	}

	[RelayCommand]
	async Task GoBackAsync()
	{
		await LoadPreviousCrumbAsync();
	}

	[RelayCommand]
	async Task NavigateBackAsync()
	{
		if (crumbs.Count > 1)
		{
			await LoadPreviousCrumbAsync();
			return;
		}

		await Shell.Current.GoToAsync("..");
	}

	async Task LoadPreviousCrumbAsync()
	{
		if (crumbs.Count <= 1)
		{
			return;
		}

		await LoadFeedAsync(crumbs[^2].Url, pushCrum: false, replaceCurrentCrumb: true);
	}

	[RelayCommand]
	async Task GoForwardAsync()
	{
		string? next = CurrentFeed?.Pagination?.NextUrl;
		if (next is null)
		{
			return;
		}

		await LoadFeedAsync(next, pushCrum: true);
	}

	[RelayCommand]
	async Task SearchAsync()
	{
		OpdsFeed? feed = CurrentFeed;
		if (feed is null)
		{
			return;
		}

		Link? search = feed.GetSearchLink();
		if (search is null)
		{
			StatusMessage = "This catalog does not offer a search interface.";
			return;
		}

		string query = SearchText.Trim();
		if (query.Length == 0)
		{
			return;
		}

		char separator = search.Href.Contains('?') ? '&' : '?';
		await LoadFeedAsync($"{search.Href}{separator}q={Uri.EscapeDataString(query)}", pushCrum: true);
	}

	OpdsFeed? CurrentFeed { get; set; }

	async Task LoadFeedAsync(string url, bool pushCrum, bool replaceCurrentCrumb = false)
	{
		if (loadCts is not null)
		{
			await loadCts.CancelAsync();
		}

		CancellationTokenSource cts = new();
		loadCts = cts;
		try
		{
			IsLoading = true;
			StatusMessage = null;

			OpdsFeed? feed = await LoadFeedCoreAsync(url, cts.Token);
			if (cts.Token.IsCancellationRequested || feed is null)
			{
				return;
			}

			if (pushCrum)
			{
				crumbs.Add(new Crumb(feed.Title, url));
			}
			else if (replaceCurrentCrumb && crumbs.Count > 1)
			{
				crumbs.RemoveAt(crumbs.Count - 1);
			}

			currentUrl = url;
			CurrentFeed = feed;
			PopulateEntries(feed);

			Title = string.IsNullOrWhiteSpace(feed.Title) ? Title : feed.Title;
			Subtitle = feed.Subtitle;
			CanGoBack = crumbs.Count > 1;
			CanGoForward = feed.Pagination?.HasNext == true;
			CanSearch = feed.GetSearchLink() is not null;
			OnPropertyChanged(nameof(Crumbs));
			OnPropertyChanged(nameof(CrumbsText));
		}
		catch (OperationCanceledException)
		{
			// Page went away; ignore.
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not load OPDS feed {Url}", url);
			StatusMessage = "Could not load this catalog: " + ex.Message;
		}
		finally
		{
			if (!cts.Token.IsCancellationRequested)
			{
				IsLoading = false;
			}
		}
	}

	async Task<OpdsFeed?> LoadFeedCoreAsync(string url, CancellationToken ct)
	{
		OpdsFeed? cached = await cache.GetAsync(url, TimeSpan.FromMinutes(5), ct);
		if (cached is not null)
		{
			return cached;
		}

		OpdsFeed feed = await parser.ParseFeedAsync(url, ct);
		feed.SourceUrl ??= url;
		await cache.SetAsync(feed, url, parentPath: Title, serverId: serverId, ct);
		return feed;
	}

	void PopulateEntries(OpdsFeed feed)
	{
		Entries.Clear();
		bool isBookFeed = feed.FeedType == FeedType.Acquisition;
		foreach (OpdsEntry entry in feed.Entries)
		{
			bool isBook = isBookFeed || EntryHasBookContent(entry);
			Entries.Add(new CatalogEntryModel(entry, isBook, this) { IsSelectable = isBook && IsSelectionMode });
		}

		HasBooks = Entries.Any(model => model.IsBook);
		if (!HasBooks)
		{
			IsSelectionMode = false;
		}

		SelectedCount = 0;
	}

	partial void OnIsSelectionModeChanged(bool value)
	{
		foreach (CatalogEntryModel entry in Entries)
		{
			entry.IsSelectable = value && entry.IsBook;
			if (!value)
			{
				entry.IsSelected = false;
			}
		}
	}

	static bool EntryHasBookContent(OpdsEntry entry)
	{
		return entry.Links.Any(link => link.IsAcquisition() &&
			(link.Type?.Contains("epub", StringComparison.OrdinalIgnoreCase) == true ||
			 link.Type?.Contains("mobi", StringComparison.OrdinalIgnoreCase) == true ||
			 link.Type?.Contains("ebook", StringComparison.OrdinalIgnoreCase) == true));
	}

	static string? GetEntryHref(OpdsEntry entry)
	{
		Link? acquisition = entry.Links.FirstOrDefault(link => link.IsAcquisition());
		return acquisition is not null ? acquisition.Href : (entry.Links.FirstOrDefault()?.Href);
	}

	public string? CurrentUrl => currentUrl;

	public void SetServerId(string? serverId) => this.serverId = serverId;

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;
		loadCts?.Cancel();
	}
}