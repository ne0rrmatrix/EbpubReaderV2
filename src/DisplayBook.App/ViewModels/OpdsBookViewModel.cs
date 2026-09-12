using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the OPDS book details page: renders the full metadata record for one
/// book and offers its acquisition (download) links.
/// </summary>
public sealed partial class OpdsBookViewModel : ObservableObject, IDisposable
{
	readonly IOpdsParserService parser;
	readonly IDownloadQueueService queue;
	readonly IBookCatalogService catalog;
	readonly INavigationService navigation;
	readonly ILogger<OpdsBookViewModel> logger;
	CancellationTokenSource? loadCts;
	bool isInLibrary;
	bool disposed;

	public OpdsBookViewModel(
		IOpdsParserService parser,
		IDownloadQueueService queue,
		IBookCatalogService catalog,
		INavigationService navigation,
		ILogger<OpdsBookViewModel> logger)
	{
		this.parser = parser;
		this.queue = queue;
		this.catalog = catalog;
		this.navigation = navigation;
		this.logger = logger;
		this.queue.ItemUpdated += OnItemUpdated;
	}

	public ObservableCollection<DownloadLink> DownloadLinks { get; } = [];

	[ObservableProperty]
	public partial string Title { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string? Subtitle { get; set; }

	[ObservableProperty]
	public partial string? CoverUrl { get; set; }

	public bool HasCover => !string.IsNullOrWhiteSpace(CoverUrl);

	partial void OnCoverUrlChanged(string? value) => OnPropertyChanged(nameof(HasCover));

	[ObservableProperty]
	public partial string? Authors { get; set; }

	[ObservableProperty]
	public partial string? Publisher { get; set; }

	public bool HasPublisher => !string.IsNullOrWhiteSpace(Publisher);

	partial void OnPublisherChanged(string? value) => OnPropertyChanged(nameof(HasPublisher));

	[ObservableProperty]
	public partial string? Published { get; set; }

	[ObservableProperty]
	public partial string? Language { get; set; }

	[ObservableProperty]
	public partial string? Series { get; set; }

	public bool HasSeries => !string.IsNullOrWhiteSpace(Series);

	partial void OnSeriesChanged(string? value) => OnPropertyChanged(nameof(HasSeries));

	[ObservableProperty]
	public partial string? Description { get; set; }

	[ObservableProperty]
	public partial string? StatusMessage { get; set; }

	[ObservableProperty]
	public partial bool IsLoading { get; set; }

	public bool IsInLibrary
	{
		get => isInLibrary;
		private set
		{
			if (SetProperty(ref isInLibrary, value))
			{
				OnPropertyChanged(nameof(HasDownloadOptions));
			}
		}
	}

	public bool HasDownloadOptions => !IsInLibrary;

	public Task InitializeAsync(string entryUrl)
	{
		disposed = false;
		loadCts?.Cancel();
		CancellationTokenSource cts = new();
		loadCts = cts;
		return LoadAsync(entryUrl, cts.Token);
	}

	public void OnPageDisappearing() => loadCts?.Cancel();

	async Task LoadAsync(string entryUrl, CancellationToken ct)
	{
		IsLoading = true;
		IsInLibrary = false;
		StatusMessage = null;
		try
		{
			BookDetails details = await LoadDetailsAsync(entryUrl, ct);
			if (ct.IsCancellationRequested || disposed)
			{
				return;
			}

			ApplyDetails(details);
			await RefreshMembershipAsync(ct);
		}
		catch (OperationCanceledException)
		{
			// Page went away; ignore.
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not load OPDS book details {Url}", entryUrl);
			if (!disposed)
			{
				StatusMessage = "Could not load this book: " + ex.Message;
			}
		}
		finally
		{
			if (!ct.IsCancellationRequested)
			{
				IsLoading = false;
			}
		}
	}

	async Task<BookDetails> LoadDetailsAsync(string entryUrl, CancellationToken ct)
	{
		// Catalog navigation stages the parsed entry because some servers (notably
		// Calibre) expose only an acquisition URL for a book. Use that metadata first
		// instead of downloading the binary EPUB and attempting to parse it as XML.
		OpdsEntry? pendingEntry = navigation.TakePendingEntry(entryUrl);
		if (pendingEntry is not null)
		{
			BookDetails stagedDetails = await parser.ParseBookDetailsAsync(pendingEntry, ct).ConfigureAwait(false);
			logger.LogDebug("Using staged OPDS entry for book details {Url}.", entryUrl);
			return stagedDetails;
		}

		try
		{
			return await parser.ParseBookDetailsAsync(entryUrl, ct).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Servers such as Calibre may not expose a separate detail document.
			OpdsEntry? fallbackEntry = navigation.TakePendingEntry(entryUrl);
			if (fallbackEntry is null)
			{
				throw;
			}

			return await parser.ParseBookDetailsAsync(fallbackEntry, ct).ConfigureAwait(false);
		}
	}

	void ApplyDetails(BookDetails details)
	{
		Title = details.Title;
		Subtitle = details.Subtitle;
		Authors = string.Join(", ", details.Authors.Where(a => !string.IsNullOrWhiteSpace(a.Name)).Select(a => a.Name));
		Publisher = details.Publisher;
		Published = details.Published?.ToString("yyyy-MM-dd");
		Language = details.Language;
		Series = details.Series;
		Description = !string.IsNullOrWhiteSpace(details.Description)
			? details.Description
			: details.Summary;
		CoverUrl = details.Cover?.ThumbnailUrl ?? details.Cover?.Url;

		DownloadLinks.Clear();
		foreach (DownloadLink link in details.DownloadLinks)
		{
			DownloadLinks.Add(link);
		}
	}

	async Task RefreshMembershipAsync(CancellationToken cancellationToken)
	{
		if (disposed || string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Authors))
		{
			return;
		}

		IReadOnlyList<BookSummary> books = await catalog.GetBooksAsync(cancellationToken);
		if (cancellationToken.IsCancellationRequested || disposed)
		{
			return;
		}

		IsInLibrary = books.Any(book => BookMetadataMatcher.Matches(book, Title, Authors));
	}

	async void OnItemUpdated(object? sender, DownloadItemEventArgs e)
	{
		if (e.Progress.Status != DownloadStatus.Completed)
		{
			return;
		}

		try
		{
			await MainThread.InvokeOnMainThreadAsync(() => RefreshMembershipAsync(CancellationToken.None));
		}
		catch (OperationCanceledException)
		{
			// The page went away while the library query was running.
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not refresh OPDS library membership for {Book}.", Title);
		}
	}

	[RelayCommand]
	async Task DownloadAsync(DownloadLink? link)
	{
		if (IsInLibrary || link is null || string.IsNullOrWhiteSpace(link.Url))
		{
			return;
		}

		StatusMessage = null;
		await EnqueueAsync(link, Title);
	}

	async Task EnqueueAsync(DownloadLink link, string bookTitle)
	{
		try
		{
			await queue.EnqueueAsync(link, bookTitle, server: null, cancellationToken: CancellationToken.None);
			if (!disposed)
			{
				StatusMessage = $"Started downloading {link.FormatName}.";
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not enqueue download for {Book}", bookTitle);
			if (!disposed)
			{
				StatusMessage = "Could not start the download: " + ex.Message;
			}
		}
	}

	[RelayCommand]
	Task GoToDownloadsAsync() => navigation.ShowDownloadsAsync();

	[RelayCommand]
	Task GoBackAsync() => navigation.GoBackAsync();

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;
		loadCts?.Cancel();
		queue.ItemUpdated -= OnItemUpdated;
	}
}