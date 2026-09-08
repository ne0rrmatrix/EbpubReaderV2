using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Opds;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the OPDS catalog page: renders one feed at a time with breadcrumbs,
/// pagination, optional search, and drills down into sub-catalogs or book details.
/// </summary>
public sealed partial class OpdsCatalogViewModel : ObservableObject, IDisposable
{
    private readonly IOpdsParserService _parser;
    private readonly IOpdsCatalogCache _cache;
    private readonly INavigationService _navigation;
    private readonly ILogger<OpdsCatalogViewModel> _logger;
    private readonly List<Crumb> _crumbs = [];
    private string? _serverId;
    private string? _currentUrl;
    private CancellationTokenSource? _loadCts;
    private bool _disposed;

    public OpdsCatalogViewModel(
        IOpdsParserService parser,
        IOpdsCatalogCache cache,
        INavigationService navigation,
        ILogger<OpdsCatalogViewModel> logger)
    {
        _parser = parser;
        _cache = cache;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<CatalogEntryModel> Entries { get; } = [];

    public IReadOnlyList<Crumb> Crumbs => _crumbs;

    /// <summary>
    /// Breadcrumb trail rendered as a single line, e.g. "Root › Fiction › Sci-Fi".
    /// </summary>
    public string CrumbsText => string.Join("  ›  ", _crumbs.Select(c => c.Title));

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
        _disposed = false;
        Title = string.IsNullOrWhiteSpace(title) ? "Catalog" : title;
        _crumbs.Clear();
        _crumbs.Add(new Crumb(Title, feedUrl));
        return LoadFeedAsync(feedUrl, pushCrum: false);
    }

    public void OnPageDisappearing() => _loadCts?.Cancel();

    internal Task OpenEntryAsync(CatalogEntryModel model)
    {
        var href = GetEntryHref(model.Entry);
        if (string.IsNullOrWhiteSpace(href))
        {
            return Task.CompletedTask;
        }

        if (model.IsBook)
        {
            return _navigation.ShowOpdsBookAsync(href, _serverId, model.Entry);
        }

        return LoadFeedAsync(href, pushCrum: true);
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        var url = _crumbs.Count > 1 ? _crumbs[^2].Url : null;
        if (url is null)
        {
            return;
        }

        await LoadFeedAsync(url, pushCrum: false);
    }

    [RelayCommand]
    private async Task NavigateBackAsync()
    {
        if (_crumbs.Count > 1)
        {
            await LoadFeedAsync(_crumbs[^2].Url, pushCrum: false);
            return;
        }

        await _navigation.GoBackAsync();
    }

    [RelayCommand]
    private async Task GoForwardAsync()
    {
        var next = CurrentFeed?.Pagination?.NextUrl;
        if (next is null)
        {
            return;
        }

        await LoadFeedAsync(next, pushCrum: true);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var feed = CurrentFeed;
        if (feed is null)
        {
            return;
        }

        var search = feed.GetSearchLink();
        if (search is null)
        {
            StatusMessage = "This catalog does not offer a search interface.";
            return;
        }

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            return;
        }

        var separator = search.Href.Contains('?') ? '&' : '?';
        await LoadFeedAsync($"{search.Href}{separator}q={Uri.EscapeDataString(query)}", pushCrum: true);
    }

    private OpdsFeed? CurrentFeed { get; set; }

    private async Task LoadFeedAsync(string url, bool pushCrum)
    {
        if (_loadCts is not null)
        {
            await _loadCts.CancelAsync();
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        try
        {
            IsLoading = true;
            StatusMessage = null;

            var feed = await LoadFeedCoreAsync(url, cts.Token);
            if (cts.Token.IsCancellationRequested || feed is null)
            {
                return;
            }

            if (pushCrum)
            {
                _crumbs.Add(new Crumb(feed.Title, url));
            }

            _currentUrl = url;
            CurrentFeed = feed;
            PopulateEntries(feed);

            Title = string.IsNullOrWhiteSpace(feed.Title) ? Title : feed.Title;
            Subtitle = feed.Subtitle;
            CanGoBack = _crumbs.Count > 1;
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
            _logger.LogWarning(ex, "Could not load OPDS feed {Url}", url);
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

    private async Task<OpdsFeed?> LoadFeedCoreAsync(string url, CancellationToken ct)
    {
        var cached = await _cache.GetAsync(url, TimeSpan.FromMinutes(5), ct);
        if (cached is not null)
        {
            return cached;
        }

        var feed = await _parser.ParseFeedAsync(url, ct);
        feed.SourceUrl = feed.SourceUrl ?? url;
        await _cache.SetAsync(feed, url, parentPath: Title, serverId: _serverId, ct);
        return feed;
    }

    private void PopulateEntries(OpdsFeed feed)
    {
        Entries.Clear();
        var isBookFeed = feed.FeedType == FeedType.Acquisition;
        foreach (var entry in feed.Entries)
        {
            var isBook = isBookFeed || EntryHasBookContent(entry);
            Entries.Add(new CatalogEntryModel(entry, isBook, this));
        }
    }

    private static bool EntryHasBookContent(OpdsEntry entry)
    {
        return entry.Links.Any(link => link.IsAcquisition() &&
            (link.Type?.Contains("epub", StringComparison.OrdinalIgnoreCase) == true ||
             link.Type?.Contains("mobi", StringComparison.OrdinalIgnoreCase) == true ||
             link.Type?.Contains("ebook", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static string? GetEntryHref(OpdsEntry entry)
    {
        var acquisition = entry.Links.FirstOrDefault(link => link.IsAcquisition());
        if (acquisition is not null)
        {
            return acquisition.Href;
        }

        return entry.Links.FirstOrDefault()?.Href;
    }

    public string? CurrentUrl => _currentUrl;

    public void SetServerId(string? serverId) => _serverId = serverId;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadCts?.Cancel();
    }
}
