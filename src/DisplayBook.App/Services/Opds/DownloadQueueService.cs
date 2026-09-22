using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services.Opds;

public sealed partial class DownloadQueueService(
	IHttpClientFactory httpClientFactory,
	IBookImportService importService,
	ILogger<DownloadQueueService> logger) : IDownloadQueueService
{
	const int bufferSize = 81920;

	static readonly TimeSpan progressInterval = TimeSpan.FromMilliseconds(250);

	readonly HttpClient http = httpClientFactory.CreateClient(OpdsConstants.HttpClientName);

	/// <summary>Ids waiting for a download slot, in the order they were enqueued.</summary>
	readonly ConcurrentQueue<string> pending = new();

	readonly Lock pumpGate = new();

	int activeWorkers;

	readonly ConcurrentDictionary<string, DownloadProgress> items = new();

	readonly ConcurrentDictionary<string, HashSet<string>> batches = new();

	readonly ConcurrentDictionary<string, DateTime> batchNotifiedAt = new();

	readonly ConcurrentDictionary<string, Task> workers = new();

	/// <summary>
	/// Created once rather than per download: enqueuing a batch of hundreds would otherwise
	/// hit the filesystem once per book before anything starts transferring.
	/// </summary>
	readonly Lazy<string> downloadsDirectory = new(() =>
	{
		string path = Path.Combine(BookStorageService.ContentRoot, "Opds", "Downloads");
		Directory.CreateDirectory(path);
		return path;
	});

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

		DownloadProgress item = CreateItem(link, bookTitle, server);
		items[item.Id] = item;
		Enqueue(item);

		return Task.FromResult(CloneProgress(item));
	}

	public Task<DownloadBatchProgress> EnqueueBatchAsync(
		IEnumerable<(string BookTitle, DownloadLink Link)> books,
		OpdsServer? server = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(books);
		cancellationToken.ThrowIfCancellationRequested();

		string batchId = Guid.NewGuid().ToString("N");
		HashSet<string> membership = [with(StringComparer.Ordinal)];
		foreach ((string? bookTitle, DownloadLink? link) in books)
		{
			DownloadProgress item = CreateItem(link, bookTitle, server);
			item.BatchId = batchId;
			items[item.Id] = item;
			membership.Add(item.Id);
			pending.Enqueue(item.Id);
		}

		// Pumped once for the whole batch rather than per book: only the first few start now,
		// and each one starts its successor as it finishes.
		PumpQueue();
		batches[batchId] = membership;
		RaiseBatchUpdated(batchId, force: true);
		return Task.FromResult(BuildBatchSnapshot(batchId, membership));
	}

	public DownloadProgress? GetItem(string itemId)
		=> items.TryGetValue(itemId, out DownloadProgress? item) ? CloneProgress(item) : null;

	public DownloadBatchProgress? GetBatch(string batchId)
		=> batches.TryGetValue(batchId, out HashSet<string>? members) ? BuildBatchSnapshot(batchId, members) : null;

	public IReadOnlyList<DownloadProgress> GetItems()
		=> [.. items.Values.Select(CloneProgress)];

	public IReadOnlyList<DownloadBatchProgress> GetBatches()
		=> [.. batches.Keys.Select(batchId => BuildBatchSnapshot(batchId, batches[batchId]))];

	public async Task PauseAsync(string itemId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (items.TryGetValue(itemId, out DownloadProgress? item) &&
			item.Status is DownloadStatus.Queued or DownloadStatus.Downloading)
		{
			item.Status = DownloadStatus.Paused;
			RaiseItemUpdated(item);
			await CancelSourceAsync(item).ConfigureAwait(false);
			logger.LogInformation("OPDS download {Item} paused.", item.BookTitle);
		}
	}

	public Task ResumeAsync(string itemId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (items.TryGetValue(itemId, out DownloadProgress? item) &&
			item.Status is DownloadStatus.Paused or DownloadStatus.Failed)
		{
			item.Status = DownloadStatus.Queued;
			item.Error = null;
			RaiseItemUpdated(item);
			Enqueue(item);
		}

		return Task.CompletedTask;
	}

	public async Task CancelAsync(string itemId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (items.TryGetValue(itemId, out DownloadProgress? item) &&
			item.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused)
		{
			item.Status = DownloadStatus.Canceled;
			RaiseItemUpdated(item);
			await CancelSourceAsync(item).ConfigureAwait(false);
			logger.LogInformation("OPDS download {Item} canceled.", item.BookTitle);
		}
	}

	/// <summary>
	/// Cancels an item's token if it has one. The status flip is what actually stops an item that
	/// has not started yet; this only matters for one already transferring.
	/// </summary>
	static async Task CancelSourceAsync(DownloadProgress item)
	{
		try
		{
			CancellationTokenSource? cts = item.Cts;
			if (cts is not null)
			{
				await cts.CancelAsync().ConfigureAwait(false);
			}
		}
		catch (ObjectDisposedException)
		{
			// The worker finished and tore its source down, or the pump is swapping in a fresh
			// one for a restart. Either way the status flip above is what the worker acts on.
		}
	}

	public Task CancelBatchAsync(string batchId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return batches.TryGetValue(batchId, out HashSet<string>? members)
			? StopAsync(members
				.Select(memberId => items.TryGetValue(memberId, out DownloadProgress? item) ? item : null)
				.OfType<DownloadProgress>())
			: Task.CompletedTask;
	}

	public Task CancelAllAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return StopAsync(items.Values);
	}

	/// <summary>
	/// Cancels many items at once. Every status is flipped in a single synchronous pass, which is
	/// all it takes for an item still waiting in <see cref="pending"/>: the pump skips anything
	/// that is no longer queued, so it never starts. Only the handful of items actually running
	/// have a token to cancel, and those are canceled off the caller's thread.
	/// </summary>
	/// <remarks>
	/// Deliberately raises no per-item <see cref="ItemUpdated"/>: cancelling one at a time and
	/// reporting each is exactly what made cancelling a large batch take the better part of a
	/// minute. Callers should re-read <see cref="GetItems"/> once this completes.
	/// </remarks>
	Task StopAsync(IEnumerable<DownloadProgress> candidates)
	{
		List<DownloadProgress> stopping = [];
		foreach (DownloadProgress item in candidates.Where(static item => item.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused))
		{
			item.Status = DownloadStatus.Canceled;
			stopping.Add(item);
		}

		if (stopping.Count == 0)
		{
			return Task.CompletedTask;
		}

		return Task.Run(() =>
		{
			foreach (DownloadProgress item in stopping)
			{
				try
				{
					item.Cts?.Cancel();
				}
				catch (ObjectDisposedException)
				{
					// The worker finished and tore its source down between the two passes.
				}
			}

			logger.LogInformation("Canceled {Count} OPDS download(s).", stopping.Count);
			foreach (string batchId in batches.Keys)
			{
				RaiseBatchUpdated(batchId, force: true);
			}
		}, CancellationToken.None);
	}

	public Task ClearFinishedAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		List<DownloadProgress> removable = [.. items.Values
			.Where(item => item.Status is DownloadStatus.Paused
				or DownloadStatus.Completed
				or DownloadStatus.Failed
				or DownloadStatus.Canceled)];
		foreach (DownloadProgress? item in removable)
		{
			items.TryRemove(item.Id, out _);
		}

		foreach (string? batchId in batches.Keys.ToList())
		{
			HashSet<string> members = batches[batchId];
			if (members.Any(memberId => items.ContainsKey(memberId)))
			{
				continue;
			}

			batches.TryRemove(batchId, out _);
		}

		return Task.CompletedTask;
	}

	public void Dispose()
	{
		List<DownloadProgress> active = [.. items.Values.Where(item => item.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused)];
		foreach (DownloadProgress? item in active)
		{
			item.Status = DownloadStatus.Canceled;
			item.Cts?.Cancel();
		}

		pending.Clear();
	}

	static DownloadProgress CreateItem(DownloadLink link, string bookTitle, OpdsServer? server)
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

	/// <summary>
	/// Queues an item for download and starts it if a slot is free.
	/// </summary>
	void Enqueue(DownloadProgress item)
	{
		pending.Enqueue(item.Id);
		PumpQueue();
	}

	/// <summary>
	/// Starts workers until the concurrency limit is reached or nothing is waiting.
	/// </summary>
	/// <remarks>
	/// Only <see cref="OpdsConstants.MaxConcurrentDownloads"/> workers exist at a time; the rest
	/// of a batch waits here as plain ids. Giving every book its own worker and cancellation
	/// source up front meant cancelling a large batch had to unwind thousands of tasks, which
	/// took the better part of a minute.
	/// </remarks>
	void PumpQueue()
	{
		while (TryTakeNext(out DownloadProgress? item))
		{
			StartWorker(item);
		}
	}

	bool TryTakeNext([NotNullWhen(true)] out DownloadProgress? next)
	{
		lock (pumpGate)
		{
			if (activeWorkers < OpdsConstants.MaxConcurrentDownloads)
			{
				// Items canceled or paused while waiting are simply skipped over here.
				while (pending.TryDequeue(out string? itemId))
				{
					if (items.TryGetValue(itemId, out DownloadProgress? item) &&
						item.Status == DownloadStatus.Queued)
					{
						// Claimed here rather than in the worker: an item can legitimately sit in
						// the queue twice (pause then resume), and the worker does not run until
						// the scheduler gets to it, so a second dequeue would otherwise still see
						// it as queued and start a duplicate transfer over the same temp file.
						item.Status = DownloadStatus.Downloading;
						activeWorkers++;
						next = item;
						return true;
					}
				}
			}
		}

		next = null;
		return false;
	}

	void StartWorker(DownloadProgress item)
	{
		item.Cts?.Dispose();
		item.Cts = new CancellationTokenSource();
		// Queue workers intentionally run in the background; RunWorkerAsync handles
		// cancellation and reports all non-cancellation failures on the item.
		CancellationToken token = item.Cts.Token;
		Task worker = Task.Run(() => RunWorkerAsync(item, token), CancellationToken.None);
		workers[item.Id] = worker;
		if (worker.IsCompleted)
		{
			workers.TryRemove(item.Id, out _);
		}
	}

	async Task RunWorkerAsync(DownloadProgress item, CancellationToken token)
	{
		string tempPath = Path.Combine(downloadsDirectory.Value, item.FileName);
		item.TempFilePath = tempPath;

		try
		{
			// TryTakeNext already moved the item to Downloading; anything else means it was
			// canceled or paused in the window between being claimed and this task running.
			if (item.Status != DownloadStatus.Downloading)
			{
				return;
			}

			item.StartedAt = DateTime.UtcNow;
			item.Error = null;
			RaiseItemUpdated(item);

			long existingLength = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0L;
			logger.LogInformation("OPDS download {Item} starting; temp partial {ExistingBytes} bytes at {TempPath}.", item.BookTitle, existingLength, tempPath);
			TransferOutcome outcome = await RequestTransferAsync(item, tempPath, existingLength, token).ConfigureAwait(false);
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
			if (item.Status is not DownloadStatus.Paused and not DownloadStatus.Canceled)
			{
				item.Status = DownloadStatus.Failed;
				item.Error = $"Download failed: {ex.Message}";
				RaiseItemUpdated(item);
				logger.LogWarning(ex, "OPDS download transfer failed for {Item}.", item.BookTitle);
			}
		}
		finally
		{
			workers.TryRemove(item.Id, out _);

			// Finished items stay in the queue until ClearFinishedAsync so the UI can show
			// their outcome, batch snapshots keep counting them, and a failed one can be retried.
			if (item.Status == DownloadStatus.Canceled)
			{
				DeletePartialFile(item, tempPath);
			}

			if (item.BatchId is not null)
			{
				RaiseBatchUpdated(item.BatchId);
			}

			lock (pumpGate)
			{
				activeWorkers--;
			}

			PumpQueue();
		}
	}

	void DeletePartialFile(DownloadProgress item, string tempPath)
	{
		try
		{
			if (File.Exists(tempPath))
			{
				File.Delete(tempPath);
			}

			item.TempFilePath = null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			logger.LogWarning(ex, "Could not delete the partial download for {Item}.", item.BookTitle);
		}
	}

	async Task<TransferOutcome> RequestTransferAsync(
		DownloadProgress item,
		string tempPath,
		long existingLength,
		CancellationToken token)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, item.Url);
		if (existingLength > 0)
		{
			request.Headers.TryAddWithoutValidation("Range", $"bytes={existingLength}-");
		}

		HttpResponseMessage response = await http
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
			long? serverLength = response.Content.Headers.ContentRange?.Length;
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

	async Task CopyBytesAsync(
		DownloadProgress item,
		HttpResponseMessage response,
		string tempPath,
		long startLength,
		CancellationToken token)
	{
		using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		long completedBytes = startLength;
		await using FileStream file = startLength > 0
			? new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize, FileOptions.Asynchronous)
			: File.Create(tempPath);
		if (startLength > 0)
		{
			file.Seek(startLength, SeekOrigin.Begin);
		}

		byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
		try
		{
			DateTime lastProgressAt = DateTime.UtcNow;
			long bytesAtLastProgress = completedBytes;
			while (true)
			{
				int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
				if (read == 0)
				{
					break;
				}

				await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
				completedBytes += read;
				SetLength(item, completedBytes, null);

				DateTime now = DateTime.UtcNow;
				double elapsed = (now - lastProgressAt).TotalSeconds;
				if (elapsed >= progressInterval.TotalSeconds)
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

	async Task ImportCompletedDownloadAsync(DownloadProgress item, string tempPath, CancellationToken token)
	{
		try
		{
			IReadOnlyList<BookSummary> importedBooks = await importService.ImportLocalFileAsync(tempPath, cancellationToken: token).ConfigureAwait(false);
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
			if (item.Status is not DownloadStatus.Paused and not DownloadStatus.Canceled)
			{
				item.Status = DownloadStatus.Failed;
				item.Error = $"Import failed: {ex.Message}";
				RaiseItemUpdated(item);
				logger.LogWarning(ex, "OPDS download import failed for {Item}.", item.BookTitle);
			}
		}
	}

	static void SetLength(DownloadProgress item, long completedBytes, long? totalBytes)
	{
		item.BytesDownloaded = completedBytes;
		if (totalBytes is not null && totalBytes.Value > 0)
		{
			item.TotalBytes = totalBytes.Value;
		}

		UpdatePercentage(item, completedBytes);
	}

	static void UpdateSpeed(DownloadProgress item, long delta, double elapsedSeconds, long completedBytes)
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

	sealed partial class TransferOutcome(HttpResponseMessage? response, long startLength, bool succeeded, bool alreadyComplete = false) : IDisposable
	{
		public HttpResponseMessage? Response { get; } = response;

		public long StartLength { get; } = startLength;

		public bool Succeeded { get; } = succeeded;

		public bool AlreadyComplete { get; } = alreadyComplete;

		public void Dispose() => Response?.Dispose();
	}

	static void FinalizeSuccess(DownloadProgress item, long completedBytes)
	{
		item.BytesDownloaded = completedBytes;
		item.Percentage = 1;
		item.Speed = 0;
		item.TimeRemaining = TimeSpan.Zero;
		item.CompletedAt = DateTime.UtcNow;
		item.Status = DownloadStatus.Completed;
	}

	static void UpdatePercentage(DownloadProgress item, long completedBytes)
	{
		if (item.TotalBytes > 0)
		{
			item.Percentage = Math.Clamp((double)completedBytes / item.TotalBytes, 0, 1);
		}
	}

	void RaiseItemUpdated(DownloadProgress item)
	{
		ItemUpdated?.Invoke(this, new DownloadItemEventArgs(CloneProgress(item)));
		if (item.BatchId is not null)
		{
			RaiseBatchUpdated(item.BatchId);
		}
	}

	/// <summary>
	/// Raises <see cref="BatchUpdated"/>, skipping the work entirely when nothing is listening
	/// and otherwise throttling to <see cref="progressInterval"/> per batch. Both matter because
	/// every worker reports on finishing and <see cref="BuildBatchSnapshot"/> walks the whole
	/// batch: a 2,000-book batch would otherwise cost 2,000 full-batch snapshots.
	/// </summary>
	/// <remarks>
	/// Throttled progress is advisory; a subscriber that needs the settled state should re-read
	/// <see cref="GetBatch"/>. <paramref name="force"/> bypasses the throttle for the enqueue and
	/// bulk-cancel snapshots, which must always land.
	/// </remarks>
	void RaiseBatchUpdated(string batchId, bool force = false)
	{
		if (BatchUpdated is null || !batches.TryGetValue(batchId, out HashSet<string>? members))
		{
			return;
		}

		DateTime now = DateTime.UtcNow;
		if (!force &&
			batchNotifiedAt.TryGetValue(batchId, out DateTime last) &&
			now - last < progressInterval)
		{
			return;
		}

		batchNotifiedAt[batchId] = now;
		BatchUpdated.Invoke(this, new DownloadBatchEventArgs(BuildBatchSnapshot(batchId, members)));
	}

	DownloadBatchProgress BuildBatchSnapshot(string batchId, IReadOnlyCollection<string> memberIds)
	{
		List<DownloadProgress> items1 = [.. memberIds
			.Select(memberId => items.TryGetValue(memberId, out DownloadProgress? item) ? item : null)
			.Where(item => item is not null)
			.Cast<DownloadProgress>()];

		return new DownloadBatchProgress
		{
			BatchId = batchId,
			TotalItems = items1.Count,
			CompletedItems = items1.Count(item => item.Status == DownloadStatus.Completed),
			FailedItems = items1.Count(item => item.Status is DownloadStatus.Failed or DownloadStatus.Canceled),
			TotalBytes = items1.Sum(item => Math.Max(item.TotalBytes, item.BytesDownloaded)),
			DownloadedBytes = items1.Sum(item => item.BytesDownloaded),
			CurrentItem = items1
				.FirstOrDefault(item => item.Status is DownloadStatus.Queued or DownloadStatus.Downloading)
				?.BookTitle
		};
	}

	static DownloadProgress CloneProgress(DownloadProgress source) => new()
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