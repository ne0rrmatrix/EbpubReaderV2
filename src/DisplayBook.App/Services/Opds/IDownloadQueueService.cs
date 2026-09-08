using DisplayBook.App.Models;

namespace DisplayBook.App.Services.Opds;

/// <summary>
/// Raised on every meaningful state transition (queued, downloading, paused,
/// completed, failed, canceled) of a single queued download. The progress
/// instance is a snapshot. Events are raised on a background thread;
/// subscribers must marshal to the UI thread.
/// </summary>
public sealed class DownloadItemEventArgs(DownloadProgress progress) : EventArgs
{
    public DownloadProgress Progress { get; } = progress;
}

/// <summary>
/// Raised whenever the aggregate state of a download batch changes.
/// The batch instance is a snapshot. Events are raised on a background thread;
/// subscribers must marshal to the UI thread.
/// </summary>
public sealed class DownloadBatchEventArgs(DownloadBatchProgress batch) : EventArgs
{
    public DownloadBatchProgress Batch { get; } = batch;
}

/// <summary>
/// A bounded-concurrency queue that downloads OPDS acquisition links into local
/// storage, reports live progress, and supports pause, resume, and cancel.
/// </summary>
public interface IDownloadQueueService : IDisposable
{
    /// <summary>Raised when a single item's state or progress changes.</summary>
    event EventHandler<DownloadItemEventArgs>? ItemUpdated;

    /// <summary>Raised when a batch's aggregate progress changes.</summary>
    event EventHandler<DownloadBatchEventArgs>? BatchUpdated;

    /// <summary>
    /// Enqueues a single acquisition link for download and returns the initial
    /// (queued) progress record.
    /// </summary>
    Task<DownloadProgress> EnqueueAsync(DownloadLink link, string bookTitle, OpdsServer? server = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues several books as a single tracked batch and returns the batch
    /// progress record. Individual items are also reported through
    /// <see cref="ItemUpdated"/>.
    /// </summary>
    Task<DownloadBatchProgress> EnqueueBatchAsync(IEnumerable<(string BookTitle, DownloadLink Link)> books, OpdsServer? server = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the current progress for the given item, or null if unknown.</summary>
    DownloadProgress? GetItem(string itemId);

    /// <summary>Returns the current progress for the given batch, or null if unknown.</summary>
    DownloadBatchProgress? GetBatch(string batchId);

    /// <summary>Returns a snapshot of every tracked item (queued, active, and finished).</summary>
    IReadOnlyList<DownloadProgress> GetItems();

    /// <summary>Returns a snapshot of every tracked batch.</summary>
    IReadOnlyList<DownloadBatchProgress> GetBatches();

    /// <summary>
    /// Pauses a download. Active items stop at the next boundary and keep their
    /// partial file so <see cref="ResumeAsync"/> can continue from that point.
    /// Queued items are marked paused before they start.
    /// </summary>
    Task PauseAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a paused or failed download, continuing from the existing
    /// partial file when the server supports byte ranges.
    /// </summary>
    Task ResumeAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a download. Queued, active, and paused items are stopped and
    /// marked canceled; the partial file (if any) is kept on disk.
    /// </summary>
    Task CancelAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>Cancels every active or queued item in the given batch.</summary>
    Task CancelBatchAsync(string batchId, CancellationToken cancellationToken = default);

    /// <summary>Removes items that are no longer active (paused/finished) from tracking.</summary>
    Task ClearFinishedAsync(CancellationToken cancellationToken = default);
}
