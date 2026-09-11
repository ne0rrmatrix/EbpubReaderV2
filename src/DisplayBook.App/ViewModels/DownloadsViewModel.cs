using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Views;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the downloads page: a live queue of active, paused, and finished
/// OPDS downloads with pause/resume/cancel controls.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
	readonly IDownloadQueueService queue;
	readonly INavigationService navigation;
	readonly ILogger<DownloadsViewModel> logger;
	readonly Dictionary<string, DownloadItemModel> byId = [];
	readonly Dictionary<string, DownloadBatchProgress> batches = [];
	bool disposed;

	public DownloadsViewModel(IDownloadQueueService queue, INavigationService navigation, ILogger<DownloadsViewModel> logger)
	{
		this.queue = queue;
		this.navigation = navigation;
		this.logger = logger;
		this.queue.ItemUpdated += OnItemUpdated;
		this.queue.BatchUpdated += OnBatchUpdated;
	}

	public ObservableCollection<DownloadItemModel> Items { get; } = [];

	public ObservableCollection<DownloadBatchProgress> Batches { get; } = [];

	[ObservableProperty]
	public partial string? StatusMessage { get; set; }

	public Task InitializeAsync()
	{
		disposed = false;
		RebuildFromSnapshot();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;
		queue.ItemUpdated -= OnItemUpdated;
		queue.BatchUpdated -= OnBatchUpdated;
	}

	void OnItemUpdated(object? sender, DownloadItemEventArgs e)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (disposed)
			{
				return;
			}

			UpdateItem(e.Progress);
		});
	}

	void OnBatchUpdated(object? sender, DownloadBatchEventArgs e)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (disposed)
			{
				return;
			}

			UpdateBatch(e.Batch);
		});
	}

	void UpdateItem(DownloadProgress progress)
	{
		if (byId.TryGetValue(progress.Id, out DownloadItemModel? model))
		{
			model.Status = progress.Status;
			model.Percentage = progress.Percentage;
			model.Error = progress.Error;
			model.BytesLabel = $"{ByteSizeConverter.FormatSize(progress.BytesDownloaded)} / {ByteSizeConverter.FormatSize(progress.TotalBytes)}";
			return;
		}

		model = new DownloadItemModel(progress, this);
		byId[progress.Id] = model;
		Items.Add(model);
	}

	void UpdateBatch(DownloadBatchProgress batch)
	{
		int index = Batches.ToList().FindIndex(b => b.BatchId == batch.BatchId);
		if (index >= 0)
		{
			Batches[index] = batch;
		}
		else
		{
			Batches.Add(batch);
		}
	}

	void RebuildFromSnapshot()
	{
		Items.Clear();
		byId.Clear();
		foreach (DownloadProgress progress in queue.GetItems())
		{
			DownloadItemModel model = new(progress, this);
			byId[progress.Id] = model;
			Items.Add(model);
		}

		Batches.Clear();
		batches.Clear();
		foreach (DownloadBatchProgress batch in queue.GetBatches())
		{
			batches[batch.BatchId] = batch;
			Batches.Add(batch);
		}
	}

	internal Task PauseAsync(string itemId) => GuardAsync(() => queue.PauseAsync(itemId));

	internal Task ResumeAsync(string itemId) => GuardAsync(() => queue.ResumeAsync(itemId));

	internal Task CancelAsync(string itemId) => GuardAsync(() => queue.CancelAsync(itemId));

	async Task GuardAsync(Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Download queue operation failed");
			StatusMessage = ex.Message;
		}
	}

	[RelayCommand]
	Task GoBackAsync() => navigation.GoBackAsync();

	[RelayCommand]
	async Task ClearFinishedAsync()
	{
		await GuardAsync(() => queue.ClearFinishedAsync());
		foreach (DownloadItemModel? model in Items.Where(m => m.IsFinished).ToList())
		{
			Items.Remove(model);
			byId.Remove(model.Id);
		}
	}

	[RelayCommand]
	async Task PauseAllAsync()
	{
		foreach (DownloadItemModel? model in Items.Where(m => m.Status is DownloadStatus.Queued or DownloadStatus.Downloading).ToList())
		{
			await PauseAsync(model.Id);
		}
	}

	[RelayCommand]
	async Task ResumeAllAsync()
	{
		foreach (DownloadItemModel? model in Items.Where(m => m.Status is DownloadStatus.Paused or DownloadStatus.Failed).ToList())
		{
			await ResumeAsync(model.Id);
		}
	}
}