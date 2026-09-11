using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the OPDS catalog page: renders one feed at a time with breadcrumbs,
/// pagination, optional search, and drills down into sub-catalogs or book details.
/// </summary>
public sealed partial class OpdsCatalogViewModel(
	IOpdsParserService parser,
	IOpdsCatalogCache cache,
	INavigationService navigation,
	ILogger<OpdsCatalogViewModel> logger) : ObservableObject, IDisposable
{
	readonly IOpdsParserService parser = parser;
	readonly IOpdsCatalogCache cache = cache;
	readonly INavigationService navigation = navigation;
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

	internal Task OpenEntryAsync(CatalogEntryModel model)
	{
		string? href = GetEntryHref(model.Entry);
		if(string.IsNullOrEmpty(href))
		{
			return Task.CompletedTask;
		}
		else if (model.IsBook)
		{
			return navigation.ShowOpdsBookAsync(href, serverId, model.Entry);
		}
		else
		{
			return LoadFeedAsync(href, pushCrum: true);
		}
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

		await navigation.GoBackAsync();
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
			Entries.Add(new CatalogEntryModel(entry, isBook, this));
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
