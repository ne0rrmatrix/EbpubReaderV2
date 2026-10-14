using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.Views;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// View model for a server row (saved or discovered) on the servers page.
/// </summary>
public sealed partial class OpdsServerDisplayModel(OpdsServer server, OpdsServersViewModel parent) : ObservableObject
{
	public OpdsServer Server { get; } = server;

	public string Name { get; } = string.IsNullOrWhiteSpace(server.Name) ? server.Url : server.Name;

	public string Url { get; } = server.Url;

	public bool IsDiscovered { get; } = server.Type == ServerType.Discovered;

	public string BadgeText => IsDiscovered ? "NETWORK" : "SAVED";

	Task? pendingEnableSave;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(OpenCommand))]
	public partial bool IsEnabled { get; set; } = server.IsEnabled;

	partial void OnIsEnabledChanged(bool value)
	{
		var previous = pendingEnableSave;
		pendingEnableSave = previous is null
			? parent.SetEnabledAsync(this, value)
			: WaitThenSaveAsync(previous, value);
	}

	async Task WaitThenSaveAsync(Task previous, bool value)
	{
		await previous;
		await parent.SetEnabledAsync(this, value);
	}

	[RelayCommand(CanExecute = nameof(CanOpen))]
	Task OpenAsync() => parent.OpenServerAsync(this);

	bool CanOpen() => IsEnabled;

	[RelayCommand]
	async Task RemoveAsync()
	{
		IAsyncRelayCommand<OpdsServerDisplayModel?> command = parent.RemoveServerCommand;
		if (!command.CanExecute(this))
		{
			return;
		}

		Task? task = command.ExecuteAsync(this);
		if (task is not null)
		{
			await task;
		}
	}
}

/// <summary>
/// View model for a single entry in an OPDS catalog feed (a book, or a sub-catalog folder).
/// </summary>
public sealed partial class CatalogEntryModel(OpdsEntry entry, bool isBook, OpdsCatalogViewModel parent) : ObservableObject
{
	public OpdsEntry Entry { get; } = entry;

	public bool IsBook { get; } = isBook;

	public string Title { get; } = string.IsNullOrWhiteSpace(entry.Title) ? "(untitled)" : entry.Title;

	public string Subtitle => IsBook
		? FormatAuthors()
		: (Entry.Summary ?? string.Empty);

	public string? CoverUrl { get; } = CalibreCoverUrl.Upgrade(
		string.IsNullOrWhiteSpace(entry.Cover?.Url) ? entry.Cover?.ThumbnailUrl : entry.Cover.Url);

	public bool HasCover => !string.IsNullOrWhiteSpace(CoverUrl);

	/// <summary>
	/// True while the catalog is in selection mode and this entry is a book, which is what
	/// puts the checkbox on the card. Sub-catalog folders are never selectable.
	/// </summary>
	[ObservableProperty]
	public partial bool IsSelectable { get; set; }

	[ObservableProperty]
	public partial bool IsSelected { get; set; }

	partial void OnIsSelectedChanged(bool value) => parent.OnEntrySelectionChanged();

	[RelayCommand]
	Task OpenAsync() => parent.OpenEntryAsync(this);

	string FormatAuthors()
	{
		List<string> names = [.. Entry.Authors
			.Where(a => !string.IsNullOrWhiteSpace(a.Name))
			.Select(a => a.Name)];

		return string.Join(", ", names);
	}
}

/// <summary>
/// View model for a single row in the download popup: one queued, running, or
/// finished OPDS download.
/// </summary>
public sealed partial class DownloadItemModel(DownloadProgress progress, DownloadCenterViewModel parent) : ObservableObject
{
	public string Id { get; } = progress.Id;

	public string BookTitle { get; } = progress.BookTitle;

	public string FileName { get; } = progress.FileName;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusText))]
	[NotifyPropertyChangedFor(nameof(IsCancellable))]
	[NotifyPropertyChangedFor(nameof(IsFinished))]
	[NotifyPropertyChangedFor(nameof(CanRetry))]
	[NotifyCanExecuteChangedFor(nameof(CancelCommand))]
	[NotifyCanExecuteChangedFor(nameof(RetryCommand))]
	public partial DownloadStatus Status { get; set; } = progress.Status;

	[ObservableProperty]
	public partial double Percentage { get; set; } = progress.Percentage;

	[ObservableProperty]
	public partial string BytesLabel { get; set; } =
		$"{ByteSizeConverter.FormatSize(progress.BytesDownloaded)} / {ByteSizeConverter.FormatSize(progress.TotalBytes)}";

	[ObservableProperty]
	public partial string? Error { get; set; } = progress.Error;

	public string StatusText => Status switch
	{
		DownloadStatus.Queued => "Queued",
		DownloadStatus.Downloading => "Downloading",
		DownloadStatus.Paused => "Paused",
		DownloadStatus.Completed => "Completed",
		DownloadStatus.Failed => "Failed",
		DownloadStatus.Canceled => "Canceled",
		_ => Status.ToString()
	};

	public bool IsCancellable => Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused;

	public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Canceled or DownloadStatus.Paused;

	public bool IsFinished => Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

	/// <summary>Copies the latest queue snapshot for this download onto the bound row.</summary>
	internal void Apply(DownloadProgress snapshot)
	{
		Status = snapshot.Status;
		Percentage = snapshot.Percentage;
		Error = snapshot.Error;
		BytesLabel = $"{ByteSizeConverter.FormatSize(snapshot.BytesDownloaded)} / {ByteSizeConverter.FormatSize(snapshot.TotalBytes)}";
	}

	[RelayCommand(CanExecute = nameof(IsCancellable))]
	Task CancelAsync() => parent.CancelAsync(Id);

	[RelayCommand(CanExecute = nameof(CanRetry))]
	Task RetryAsync() => parent.RetryAsync(Id);
}
