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
/// View model for a single item in the download queue.
/// </summary>
public sealed partial class DownloadItemModel(DownloadProgress progress, DownloadsViewModel parent) : ObservableObject
{
	public string Id { get; } = progress.Id;

	public string BookTitle { get; } = progress.BookTitle;

	public string FileName { get; } = progress.FileName;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(StatusText))]
	[NotifyPropertyChangedFor(nameof(PauseResumeButtonText))]
	[NotifyPropertyChangedFor(nameof(IsCancellable))]
	[NotifyCanExecuteChangedFor(nameof(PauseResumeCommand))]
	[NotifyCanExecuteChangedFor(nameof(CancelCommand))]
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

	public bool IsPausedOrFailed => Status is DownloadStatus.Paused or DownloadStatus.Failed;

	public bool IsCancellable => Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused;

	public string PauseResumeButtonText => IsPausedOrFailed ? "Resume" : "Pause";

	public bool IsFinished => Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled;

	[RelayCommand(CanExecute = nameof(CanPauseResume))]
	Task PauseResumeAsync()
	{
		return Status is DownloadStatus.Paused or DownloadStatus.Failed
			? parent.ResumeAsync(Id)
			: parent.PauseAsync(Id);
	}

	bool CanPauseResume() => IsCancellable || IsPausedOrFailed;

	[RelayCommand(CanExecute = nameof(CanCancel))]
	Task CancelAsync() => parent.CancelAsync(Id);

	bool CanCancel() => IsCancellable;
}
