using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Opds;
using DisplayBook.App.Views;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the downloads page: a live queue of active, paused, and finished
/// OPDS downloads with pause/resume/cancel controls.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly IDownloadQueueService _queue;
    private readonly INavigationService _navigation;
    private readonly ILogger<DownloadsViewModel> _logger;
    private readonly Dictionary<string, DownloadItemModel> _byId = [];
    private readonly Dictionary<string, DownloadBatchProgress> _batches = [];
    private bool _disposed;

    public DownloadsViewModel(IDownloadQueueService queue, INavigationService navigation, ILogger<DownloadsViewModel> logger)
    {
        _queue = queue;
        _navigation = navigation;
        _logger = logger;
        _queue.ItemUpdated += OnItemUpdated;
        _queue.BatchUpdated += OnBatchUpdated;
    }

    public ObservableCollection<DownloadItemModel> Items { get; } = [];

    public ObservableCollection<DownloadBatchProgress> Batches { get; } = [];

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public Task InitializeAsync()
    {
        _disposed = false;
        RebuildFromSnapshot();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.ItemUpdated -= OnItemUpdated;
        _queue.BatchUpdated -= OnBatchUpdated;
    }

    private void OnItemUpdated(object? sender, DownloadItemEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            UpdateItem(e.Progress);
        });
    }

    private void OnBatchUpdated(object? sender, DownloadBatchEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            UpdateBatch(e.Batch);
        });
    }

    private void UpdateItem(DownloadProgress progress)
    {
        if (_byId.TryGetValue(progress.Id, out var model))
        {
            model.Status = progress.Status;
            model.Percentage = progress.Percentage;
            model.Error = progress.Error;
            model.BytesLabel = $"{ByteSizeConverter.FormatSize(progress.BytesDownloaded)} / {ByteSizeConverter.FormatSize(progress.TotalBytes)}";
            return;
        }

        model = new DownloadItemModel(progress, this);
        _byId[progress.Id] = model;
        Items.Add(model);
    }

    private void UpdateBatch(DownloadBatchProgress batch)
    {
        var index = Batches.ToList().FindIndex(b => b.BatchId == batch.BatchId);
        if (index >= 0)
        {
            Batches[index] = batch;
        }
        else
        {
            Batches.Add(batch);
        }
    }

    private void RebuildFromSnapshot()
    {
        Items.Clear();
        _byId.Clear();
        foreach (var progress in _queue.GetItems())
        {
            var model = new DownloadItemModel(progress, this);
            _byId[progress.Id] = model;
            Items.Add(model);
        }

        Batches.Clear();
        _batches.Clear();
        foreach (var batch in _queue.GetBatches())
        {
            _batches[batch.BatchId] = batch;
            Batches.Add(batch);
        }
    }

    internal Task PauseAsync(string itemId) => GuardAsync(() => _queue.PauseAsync(itemId));

    internal Task ResumeAsync(string itemId) => GuardAsync(() => _queue.ResumeAsync(itemId));

    internal Task CancelAsync(string itemId) => GuardAsync(() => _queue.CancelAsync(itemId));

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Download queue operation failed");
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private Task GoBackAsync() => _navigation.GoBackAsync();

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        await GuardAsync(() => _queue.ClearFinishedAsync());
        foreach (var model in Items.Where(m => m.IsFinished).ToList())
        {
            Items.Remove(model);
            _byId.Remove(model.Id);
        }
    }

    [RelayCommand]
    private async Task PauseAllAsync()
    {
        foreach (var model in Items.Where(m => m.Status is DownloadStatus.Queued or DownloadStatus.Downloading).ToList())
        {
            await PauseAsync(model.Id);
        }
    }

    [RelayCommand]
    private async Task ResumeAllAsync()
    {
        foreach (var model in Items.Where(m => m.Status is DownloadStatus.Paused or DownloadStatus.Failed).ToList())
        {
            await ResumeAsync(model.Id);
        }
    }
}
