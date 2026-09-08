using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services.Opds;

public sealed class DownloadQueueService(
    IHttpClientFactory httpClientFactory,
    BookStorageService storage,
    IBookImportService importService,
    ILogger<DownloadQueueService> logger) : IDownloadQueueService
{
    private const int BufferSize = 81920;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http = httpClientFactory.CreateClient(OpdsConstants.HttpClientName);

    private readonly SemaphoreSlim _slots = new(OpdsConstants.MaxConcurrentDownloads, OpdsConstants.MaxConcurrentDownloads);

    private readonly ConcurrentDictionary<string, DownloadProgress> _items = new();

    private readonly ConcurrentDictionary<string, HashSet<string>> _batches = new();

    private readonly ConcurrentDictionary<string, Task> _workers = new();

    public event EventHandler<DownloadItemEventArgs>? ItemUpdated;

    public event EventHandler<DownloadBatchEventArgs>? BatchUpdated;

    public Task<DownloadProgress> EnqueueAsync(
        DownloadLink link,
        string bookTitle,
        OpdsServer? server = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        cancellationToken.ThrowIfCancellationRequested();

        var item = CreateItem(link, bookTitle, server);
        _items[item.Id] = item;
        StartWorker(item);

        return Task.FromResult(CloneProgress(item));
    }

    public Task<DownloadBatchProgress> EnqueueBatchAsync(
        IEnumerable<(string BookTitle, DownloadLink Link)> books,
        OpdsServer? server = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        cancellationToken.ThrowIfCancellationRequested();

        var batchId = Guid.NewGuid().ToString("N");
        var membership = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (bookTitle, link) in books)
        {
            var item = CreateItem(link, bookTitle, server);
            item.BatchId = batchId;
            _items[item.Id] = item;
            membership.Add(item.Id);
            StartWorker(item);
        }

        _batches[batchId] = membership;
        RaiseBatchUpdated(batchId);
        return Task.FromResult(BuildBatchSnapshot(batchId, membership));
    }

    public DownloadProgress? GetItem(string itemId)
        => _items.TryGetValue(itemId, out var item) ? CloneProgress(item) : null;

    public DownloadBatchProgress? GetBatch(string batchId)
        => _batches.TryGetValue(batchId, out var members) ? BuildBatchSnapshot(batchId, members) : null;

    public IReadOnlyList<DownloadProgress> GetItems()
        => _items.Values.Select(CloneProgress).ToList();

    public IReadOnlyList<DownloadBatchProgress> GetBatches()
        => _batches.Keys
            .Select(batchId => BuildBatchSnapshot(batchId, _batches[batchId]))
            .ToList();

    public async Task PauseAsync(string itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_items.TryGetValue(itemId, out var item) &&
            item.Status is DownloadStatus.Queued or DownloadStatus.Downloading)
        {
            item.Status = DownloadStatus.Paused;
            RaiseItemUpdated(item);
            if (item.Cts is not null)
            {
                await item.Cts.CancelAsync().ConfigureAwait(false);
            }

            logger.LogInformation("OPDS download {Item} paused.", item.BookTitle);
        }
    }

    public Task ResumeAsync(string itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_items.TryGetValue(itemId, out var item) &&
            item.Status is DownloadStatus.Paused or DownloadStatus.Failed)
        {
            item.Status = DownloadStatus.Queued;
            item.Error = null;
            RaiseItemUpdated(item);
            StartWorker(item);
        }

        return Task.CompletedTask;
    }

    public async Task CancelAsync(string itemId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_items.TryGetValue(itemId, out var item) &&
            item.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused)
        {
            item.Status = DownloadStatus.Canceled;
            RaiseItemUpdated(item);
            if (item.Cts is not null)
            {
                await item.Cts.CancelAsync().ConfigureAwait(false);
            }

            logger.LogInformation("OPDS download {Item} canceled.", item.BookTitle);
        }
    }

    public async Task CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_batches.TryGetValue(batchId, out var members))
        {
            foreach (var memberId in members.ToList())
            {
                await CancelAsync(memberId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task ClearFinishedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removable = _items.Values
            .Where(item => item.Status is DownloadStatus.Paused
                or DownloadStatus.Completed
                or DownloadStatus.Failed
                or DownloadStatus.Canceled)
            .ToList();
        foreach (var item in removable)
        {
            _items.TryRemove(item.Id, out _);
        }

        foreach (var batchId in _batches.Keys.ToList())
        {
            var members = _batches[batchId];
            if (members.Any(memberId => _items.ContainsKey(memberId)))
            {
                continue;
            }

            _batches.TryRemove(batchId, out _);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        var active = _items.Values
            .Where(item => item.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused)
            .ToList();
        foreach (var item in active)
        {
            item.Status = DownloadStatus.Canceled;
            item.Cts?.Cancel();
        }

        _slots.Dispose();
    }

    private static DownloadProgress CreateItem(DownloadLink link, string bookTitle, OpdsServer? server)
        => new()
        {
            BookTitle = string.IsNullOrWhiteSpace(bookTitle) ? link.Title ?? bookTitle : bookTitle,
            FileName = DownloadFileNaming.GetFileName(link, bookTitle),
            Url = link.Url,
            ServerId = server?.Id,
            TotalBytes = link.Size,
            Status = DownloadStatus.Queued,
            StartedAt = DateTime.UtcNow
        };

    private void StartWorker(DownloadProgress item)
    {
        item.Cts?.Dispose();
        item.Cts = new CancellationTokenSource();
        // Queue workers intentionally run in the background; RunWorkerAsync handles
        // cancellation and reports all non-cancellation failures on the item.
        var worker = RunWorkerAsync(item, item.Cts.Token);
        _workers[item.Id] = worker;
        if (worker.IsCompleted)
        {
            _workers.TryRemove(item.Id, out _);
        }
    }

    private async Task RunWorkerAsync(DownloadProgress item, CancellationToken token)
    {
        var downloadsDir = Path.Combine(storage.ContentRoot, "Opds", "Downloads");
        var tempPath = Path.Combine(downloadsDir, item.FileName);
        Directory.CreateDirectory(downloadsDir);
        item.TempFilePath = tempPath;

        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (item.Status != DownloadStatus.Queued)
            {
                return;
            }

            item.Status = DownloadStatus.Downloading;
            item.StartedAt = DateTime.UtcNow;
            item.Error = null;
            RaiseItemUpdated(item);

            var existingLength = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;
            logger.LogInformation("OPDS download {Item} starting; temp partial {ExistingBytes} bytes at {TempPath}.", item.BookTitle, existingLength, tempPath);
            var outcome = await RequestTransferAsync(item, tempPath, existingLength, token).ConfigureAwait(false);
            try
            {
                if (!outcome.Succeeded)
                {
                    return;
                }

                if (!outcome.AlreadyComplete)
                {
                    await CopyBytesAsync(item, outcome.Response!, tempPath, outcome.StartLength, token).ConfigureAwait(false);
                }

                await ImportCompletedDownloadAsync(item, tempPath, token).ConfigureAwait(false);
            }
            finally
            {
                outcome.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // Status was already set by PauseAsync/CancelAsync before cancellation.
        }
        catch (Exception ex)
        {
            if (item.Status != DownloadStatus.Paused && item.Status != DownloadStatus.Canceled)
            {
                item.Status = DownloadStatus.Failed;
                item.Error = $"Download failed: {ex.Message}";
                RaiseItemUpdated(item);
                logger.LogWarning(ex, "OPDS download transfer failed for {Item}.", item.BookTitle);
            }
        }
        finally
        {
            _workers.TryRemove(item.Id, out _);
            _slots.Release();

            if (item.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Canceled)
            {
                _items.TryRemove(item.Id, out _);
            }

            if (item.BatchId is not null)
            {
                RaiseBatchUpdated(item.BatchId);
            }
        }
    }

    private async Task<TransferOutcome> RequestTransferAsync(
        DownloadProgress item,
        string tempPath,
        long existingLength,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (existingLength > 0)
        {
            request.Headers.TryAddWithoutValidation("Range", $"bytes={existingLength}-");
        }

        var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (response.Content.Headers.ContentLength is long chunk && chunk > 0)
            {
                item.TotalBytes = existingLength + chunk;
            }

            return new TransferOutcome(response, existingLength, succeeded: true);
        }

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength > 0)
        {
            var serverLength = response.Content.Headers.ContentRange?.Length;
            if (serverLength == existingLength)
            {
                // A 416 is only completion when the server confirms that the partial
                // file length is exactly the resource length. This avoids promoting a
                // truncated file when the server rejects an otherwise valid range.
                response.Dispose();
                SetLength(item, existingLength, existingLength);
                item.TotalBytes = existingLength;
                logger.LogInformation("OPDS download {Item}: partial is already complete at {Bytes} bytes.", item.BookTitle, existingLength);
                return new TransferOutcome(null, existingLength, succeeded: true, alreadyComplete: true);
            }

            // The partial file is stale or the server did not provide enough
            // information to validate it. Restart without a Range header.
            response.Dispose();
            File.Delete(tempPath);
            logger.LogWarning("OPDS download {Item}: server rejected resume at {ExistingBytes} bytes (server length {ServerBytes}); restarting.", item.BookTitle, existingLength, serverLength);
            return await RequestTransferAsync(item, tempPath, existingLength: 0, token).ConfigureAwait(false);
        }

        if (!response.IsSuccessStatusCode)
        {
            item.Status = DownloadStatus.Failed;
            item.Error = $"Download failed: server responded {(int)response.StatusCode} ({response.StatusCode}).";
            RaiseItemUpdated(item);
            logger.LogWarning("OPDS download transfer failed for {Item}: {StatusCode} {Reason}.", item.BookTitle, (int)response.StatusCode, response.ReasonPhrase);
            return new TransferOutcome(response, 0, succeeded: false);
        }

        if (existingLength > 0)
        {
            // 200 OK: the server ignored the Range header; restart from scratch.
            SetLength(item, 0, response.Content.Headers.ContentLength);
            return new TransferOutcome(response, 0, succeeded: true);
        }

        SetLength(item, existingLength, response.Content.Headers.ContentLength);
        return new TransferOutcome(response, existingLength, succeeded: true);
    }

    private async Task CopyBytesAsync(
        DownloadProgress item,
        HttpResponseMessage response,
        string tempPath,
        long startLength,
        CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var completedBytes = startLength;
        await using (var file = startLength > 0
            ? new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.Asynchronous)
            : File.Create(tempPath))
        {
            if (startLength > 0)
            {
                file.Seek(startLength, SeekOrigin.Begin);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                var lastProgressAt = DateTime.UtcNow;
                var bytesAtLastProgress = completedBytes;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    completedBytes += read;
                    SetLength(item, completedBytes, null);

                    var now = DateTime.UtcNow;
                    var elapsed = (now - lastProgressAt).TotalSeconds;
                    if (elapsed >= ProgressInterval.TotalSeconds)
                    {
                        UpdateSpeed(item, completedBytes - bytesAtLastProgress, elapsed, completedBytes);
                        lastProgressAt = now;
                        bytesAtLastProgress = completedBytes;
                        RaiseItemUpdated(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private async Task ImportCompletedDownloadAsync(DownloadProgress item, string tempPath, CancellationToken token)
    {
        try
        {
            var importedBooks = await importService.ImportLocalFileAsync(tempPath, cancellationToken: token).ConfigureAwait(false);
            File.Delete(tempPath);
            FinalizeSuccess(item, item.BytesDownloaded);
            RaiseItemUpdated(item);
            if (importedBooks.Count == 0)
            {
                logger.LogInformation("OPDS download {Item} matched an existing library book; removed duplicate staging file.", item.BookTitle);
            }
            else
            {
                logger.LogInformation("OPDS download {Item} imported into the library.", item.BookTitle);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (item.Status != DownloadStatus.Paused && item.Status != DownloadStatus.Canceled)
            {
                item.Status = DownloadStatus.Failed;
                item.Error = $"Import failed: {ex.Message}";
                RaiseItemUpdated(item);
                logger.LogWarning(ex, "OPDS download import failed for {Item}.", item.BookTitle);
            }
        }
    }

    private static void SetLength(DownloadProgress item, long completedBytes, long? totalBytes)
    {
        item.BytesDownloaded = completedBytes;
        if (totalBytes is not null && totalBytes.Value > 0)
        {
            item.TotalBytes = totalBytes.Value;
        }

        UpdatePercentage(item, completedBytes);
    }

    private static void UpdateSpeed(DownloadProgress item, long delta, double elapsedSeconds, long completedBytes)
    {
        if (elapsedSeconds <= 0)
        {
            return;
        }

        item.Speed = delta / elapsedSeconds;
        if (item.TotalBytes > 0 && item.Speed > 0)
        {
            item.TimeRemaining = TimeSpan.FromSeconds((item.TotalBytes - completedBytes) / item.Speed);
        }
    }

    private sealed class TransferOutcome(HttpResponseMessage? response, long startLength, bool succeeded, bool alreadyComplete = false) : IDisposable
    {
        public HttpResponseMessage? Response { get; } = response;

        public long StartLength { get; } = startLength;

        public bool Succeeded { get; } = succeeded;

        public bool AlreadyComplete { get; } = alreadyComplete;

        public void Dispose() => Response?.Dispose();
    }

    private static void FinalizeSuccess(DownloadProgress item, long completedBytes)
    {
        item.BytesDownloaded = completedBytes;
        item.Percentage = 1;
        item.Speed = 0;
        item.TimeRemaining = TimeSpan.Zero;
        item.CompletedAt = DateTime.UtcNow;
        item.Status = DownloadStatus.Completed;
    }

    private static void UpdatePercentage(DownloadProgress item, long completedBytes)
    {
        if (item.TotalBytes > 0)
        {
            item.Percentage = Math.Clamp((double)completedBytes / item.TotalBytes, 0, 1);
        }
    }

    private void RaiseItemUpdated(DownloadProgress item)
    {
        ItemUpdated?.Invoke(this, new DownloadItemEventArgs(CloneProgress(item)));
        if (item.BatchId is not null)
        {
            RaiseBatchUpdated(item.BatchId);
        }
    }

    private void RaiseBatchUpdated(string batchId)
    {
        if (_batches.TryGetValue(batchId, out var members))
        {
            BatchUpdated?.Invoke(this, new DownloadBatchEventArgs(BuildBatchSnapshot(batchId, members)));
        }
    }

    private DownloadBatchProgress BuildBatchSnapshot(string batchId, IReadOnlyCollection<string> memberIds)
    {
        var items = memberIds
            .Select(memberId => _items.TryGetValue(memberId, out var item) ? item : null)
            .Where(item => item is not null)
            .Cast<DownloadProgress>()
            .ToList();

        return new DownloadBatchProgress
        {
            BatchId = batchId,
            TotalItems = items.Count,
            CompletedItems = items.Count(item => item.Status == DownloadStatus.Completed),
            FailedItems = items.Count(item => item.Status is DownloadStatus.Failed or DownloadStatus.Canceled),
            TotalBytes = items.Sum(item => Math.Max(item.TotalBytes, item.BytesDownloaded)),
            DownloadedBytes = items.Sum(item => item.BytesDownloaded),
            CurrentItem = items
                .FirstOrDefault(item => item.Status is DownloadStatus.Queued or DownloadStatus.Downloading)
                ?.BookTitle
        };
    }

    private static DownloadProgress CloneProgress(DownloadProgress source) => new()
    {
        Id = source.Id,
        BookTitle = source.BookTitle,
        FileName = source.FileName,
        Url = source.Url,
        ServerId = source.ServerId,
        BytesDownloaded = source.BytesDownloaded,
        TotalBytes = source.TotalBytes,
        Percentage = source.Percentage,
        Speed = source.Speed,
        TimeRemaining = source.TimeRemaining,
        Status = source.Status,
        Error = source.Error,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        TempFilePath = source.TempFilePath,
        BatchId = source.BatchId
    };
}
