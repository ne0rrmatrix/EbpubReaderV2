using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Opds;
using DisplayBook.DeviceTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace DisplayBook.DeviceTests.Tests;

/// <summary>
/// The OPDS download queue on its own: a fresh queue per test, a scripted HTTP handler and a fake
/// importer, covering resume (Range/206/416), pause/resume/cancel mid-transfer, concurrency and batches.
/// </summary>
public sealed partial class DownloadQueueServiceTests : AppDataTest, IDisposable
{
	static readonly byte[] book = [.. Enumerable.Range(0, 200_000).Select(index => (byte)(index % 251))];

	readonly ScriptedHandler handler = new();
	readonly FakeImporter importer = new();
	readonly DownloadQueueService queue;
	readonly ConcurrentQueue<DownloadProgress> updates = new();

	public DownloadQueueServiceTests()
	{
		queue = new DownloadQueueService(new SingleClientFactory(handler), importer, NullLogger<DownloadQueueService>.Instance);
		queue.ItemUpdated += (_, e) => updates.Enqueue(e.Progress);
	}

	public void Dispose() => queue.Dispose();

	static DownloadLink Link(int id) => new() { Url = $"http://calibre.test/get/epub/{id}", Format = "application/epub+zip", Size = book.Length };

	static string PartialPath(int id, string title) =>
		Path.Combine(BookStorageService.ContentRoot, "Opds", "Downloads", DownloadFileNaming.GetFileName(Link(id), title));

	async Task<DownloadProgress> WaitForStatusAsync(string itemId, params DownloadStatus[] statuses)
	{
		try
		{
			await Wait.UntilAsync(() => queue.GetItem(itemId) is { } item && statuses.Contains(item.Status), $"the download to reach {string.Join(" or ", statuses)}");
		}
		catch (TimeoutException exception)
		{
			DownloadProgress? item = queue.GetItem(itemId);
			throw new TimeoutException($"{exception.Message} It is {item?.Status} ({item?.Error}).", exception);
		}

		return queue.GetItem(itemId)!;
	}

	[Fact]
	public async Task Enqueue_DownloadsAndImportsTheBook()
	{
		handler.Respond = _ => Content(HttpStatusCode.OK, book);

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");
		DownloadProgress done = await WaitForStatusAsync(queued.Id, DownloadStatus.Completed);

		Assert.Equal((1d, book.LongLength, book.LongLength), (done.Percentage, done.BytesDownloaded, done.TotalBytes));
		Assert.NotNull(done.CompletedAt);
		Assert.Equal(book, Assert.Single(importer.Imported));
		Assert.False(File.Exists(PartialPath(1, "Elric")));
		Assert.Contains(updates, update => update.Status == DownloadStatus.Downloading);
		Assert.Single(queue.GetItems());
	}

	[Fact]
	public async Task Enqueue_DuplicateOfALibraryBook_StillCompletes()
	{
		handler.Respond = _ => Content(HttpStatusCode.OK, book);
		importer.ReturnNothing = true;

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");

		await WaitForStatusAsync(queued.Id, DownloadStatus.Completed);
	}

	[Theory]
	[InlineData(HttpStatusCode.NotFound, "Download failed: server responded 404 (NotFound).")]
	[InlineData(HttpStatusCode.Unauthorized, "Download failed: server responded 401 (Unauthorized).")]
	public async Task Enqueue_ServerError_Fails(HttpStatusCode status, string error)
	{
		handler.Respond = _ => new HttpResponseMessage(status);

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");

		Assert.Equal(error, (await WaitForStatusAsync(queued.Id, DownloadStatus.Failed)).Error);
	}

	[Fact]
	public async Task Enqueue_NetworkError_Fails()
	{
		handler.Respond = _ => throw new HttpRequestException("connection refused");

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");

		Assert.Equal("Download failed: connection refused", (await WaitForStatusAsync(queued.Id, DownloadStatus.Failed)).Error);
	}

	[Fact]
	public async Task Enqueue_ImportFails_MarksTheDownloadFailed()
	{
		handler.Respond = _ => Content(HttpStatusCode.OK, book);
		importer.Failure = new InvalidDataException("not an EPUB");

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");

		Assert.Equal("Import failed: not an EPUB", (await WaitForStatusAsync(queued.Id, DownloadStatus.Failed)).Error);
	}

	[Fact]
	public async Task PartialFile_IsResumedWithARangeRequest()
	{
		int half = book.Length / 2;
		await File.WriteAllBytesAsync(PartialPath(1, "Elric"), book[..half]);
		handler.Respond = request =>
		{
			Assert.Equal($"bytes={half}-", request.Headers.Range?.ToString());
			return Content(HttpStatusCode.PartialContent, book[half..]);
		};

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");

		Assert.Equal(book.LongLength, (await WaitForStatusAsync(queued.Id, DownloadStatus.Completed)).TotalBytes);
		Assert.Equal(book, Assert.Single(importer.Imported));
	}

	[Fact]
	public async Task PartialFile_ServerIgnoresTheRange_StartsOver()
	{
		await File.WriteAllBytesAsync(PartialPath(1, "Elric"), book[..1000]);
		handler.Respond = _ => Content(HttpStatusCode.OK, book);

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");

		await WaitForStatusAsync(queued.Id, DownloadStatus.Completed);
		Assert.Equal(book, Assert.Single(importer.Imported));
	}

	[Fact]
	public async Task PartialFile_AlreadyComplete_IsImportedWithoutDownloading()
	{
		await File.WriteAllBytesAsync(PartialPath(1, "Elric"), book);
		handler.Respond = _ => RangeNotSatisfiable(book.Length);

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");

		await WaitForStatusAsync(queued.Id, DownloadStatus.Completed);
		Assert.Equal(book, Assert.Single(importer.Imported));
		Assert.Single(handler.Requests);
	}

	[Fact]
	public async Task PartialFile_RejectedRange_IsDiscardedAndRestarted()
	{
		await File.WriteAllBytesAsync(PartialPath(1, "Elric"), book[..1000]);
		handler.Respond = request => request.Headers.Range is null ? Content(HttpStatusCode.OK, book) : RangeNotSatisfiable(5);

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");

		await WaitForStatusAsync(queued.Id, DownloadStatus.Completed);
		Assert.Equal(book, Assert.Single(importer.Imported));
		Assert.Equal(2, handler.Requests.Count);
	}

	[Fact]
	public async Task PauseThenResume_ContinuesFromThePartialFile()
	{
		GatedContent first = new(book, sendFirst: 50_000);
		int requests = 0;
		handler.Respond = request => request.Headers.Range is { } range
			? Content(HttpStatusCode.PartialContent, book[(int)range.Ranges.First().From!.Value..])
			: Interlocked.Increment(ref requests) == 1
				? new HttpResponseMessage(HttpStatusCode.OK) { Content = first }
				: Content(HttpStatusCode.OK, book);

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");
		await Wait.UntilAsync(() => queue.GetItem(queued.Id)!.BytesDownloaded >= 50_000, "the first chunk to be written");
		await queue.PauseAsync(queued.Id);
		DownloadProgress paused = await WaitForStatusAsync(queued.Id, DownloadStatus.Paused);
		await Wait.UntilAsync(() => File.Exists(PartialPath(1, "Elric")), "the partial file to be kept");

		// The paused transfer closes its file just after the status changes; resuming inside that
		// window fails with the file still in use (retrying then works), so wait for it.
		await Wait.UntilAsync(() => IsFree(PartialPath(1, "Elric")), "the paused transfer to close its file");
		await queue.ResumeAsync(queued.Id);

		await WaitForStatusAsync(queued.Id, DownloadStatus.Completed);
		Assert.Equal(DownloadStatus.Paused, paused.Status);
		Assert.Equal(book, Assert.Single(importer.Imported));
		Assert.NotNull(handler.Requests[^1].Headers.Range);
	}

	[Fact]
	public async Task Cancel_StopsTheTransferAndDeletesThePartialFile()
	{
		GatedContent content = new(book, sendFirst: 50_000);
		handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content };

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");
		await content.FirstChunkSent;
		await queue.CancelAsync(queued.Id);

		await WaitForStatusAsync(queued.Id, DownloadStatus.Canceled);
		await Wait.UntilAsync(() => !File.Exists(PartialPath(1, "Elric")), "the partial file to be deleted");
		Assert.Empty(importer.Imported);

		// A finished download can't be paused, resumed or cancelled again.
		await queue.PauseAsync(queued.Id);
		await queue.CancelAsync(queued.Id);
		Assert.Equal(DownloadStatus.Canceled, queue.GetItem(queued.Id)!.Status);
	}

	[Fact]
	public async Task FailedDownload_CanBeRetried()
	{
		int attempts = 0;
		handler.Respond = _ => ++attempts == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Content(HttpStatusCode.OK, book);

		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");
		await WaitForStatusAsync(queued.Id, DownloadStatus.Failed);
		await queue.ResumeAsync(queued.Id);

		DownloadProgress done = await WaitForStatusAsync(queued.Id, DownloadStatus.Completed);
		Assert.Null(done.Error);
	}

	[Fact]
	public async Task OnlyThreeDownloadsRunAtOnce()
	{
		List<GatedContent> transfers = [];
		handler.Respond = _ =>
		{
			GatedContent content = new(book, sendFirst: 1000);
			lock (transfers)
			{
				transfers.Add(content);
			}

			return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
		};

		DownloadProgress[] items = await Task.WhenAll(Enumerable.Range(1, 4).Select(id => queue.EnqueueAsync(Link(id), $"Book {id}")));
		await Wait.UntilAsync(() => handler.Requests.Count == OpdsConstants.MaxConcurrentDownloads, "three downloads to start");
		await Task.Delay(200);
		Assert.Equal(OpdsConstants.MaxConcurrentDownloads, handler.Requests.Count);
		Assert.Equal(DownloadStatus.Queued, queue.GetItem(items[3].Id)!.Status);

		// Cancelled while still waiting: it never starts.
		await queue.CancelAsync(items[3].Id);
		lock (transfers)
		{
			transfers.ForEach(transfer => transfer.Release());
		}

		await Task.WhenAll(items[..3].Select(item => WaitForStatusAsync(item.Id, DownloadStatus.Completed)));
		Assert.Equal(OpdsConstants.MaxConcurrentDownloads, handler.Requests.Count);
		Assert.Equal(DownloadStatus.Canceled, queue.GetItem(items[3].Id)!.Status);
	}

	[Fact]
	public async Task Batch_ReportsProgressAndCanBeCancelled()
	{
		List<DownloadBatchProgress> batchUpdates = [];
		queue.BatchUpdated += (_, e) =>
		{
			lock (batchUpdates)
			{
				batchUpdates.Add(e.Batch);
			}
		};
		GatedContent blocked = new(book, sendFirst: 1000);
		handler.Respond = request => request.RequestUri!.AbsoluteUri.EndsWith("/2", StringComparison.Ordinal)
			? new HttpResponseMessage(HttpStatusCode.OK) { Content = blocked }
			: Content(HttpStatusCode.OK, book);

		DownloadBatchProgress batch = await queue.EnqueueBatchAsync([("Book 1", Link(1)), ("Book 2", Link(2)), ("Book 3", Link(3))]);
		Assert.Equal(3, batch.TotalItems);
		await Wait.UntilAsync(() => queue.GetBatch(batch.BatchId)!.CompletedItems == 2, "two of the batch to finish");
		DownloadBatchProgress partway = queue.GetBatch(batch.BatchId)!;
		Assert.Equal("Book 2", partway.CurrentItem);
		Assert.InRange(partway.Percentage, 0.01, 0.99);

		await queue.CancelBatchAsync(batch.BatchId);
		await Wait.UntilAsync(() => queue.GetBatch(batch.BatchId)!.FailedItems == 1, "the cancelled item to count as failed");
		Assert.Single(queue.GetBatches());
		Assert.NotEmpty(batchUpdates);

		await queue.CancelBatchAsync("no such batch");
		Assert.Null(queue.GetBatch("no such batch"));
	}

	[Fact]
	public async Task CancelAllThenClearFinished_EmptiesTheQueue()
	{
		// A body per request: HttpContent caches the stream it hands out, so one body shared by
		// all three transfers would have them reading over each other, and a transfer that ends
		// early or fails is no longer cancellable.
		handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new GatedContent(book, sendFirst: 1000) };
		await queue.EnqueueBatchAsync([("Book 1", Link(1)), ("Book 2", Link(2))]);
		DownloadProgress single = await queue.EnqueueAsync(Link(3), "Book 3");
		await Wait.UntilAsync(() => handler.Requests.Count > 0, "a download to start");

		await queue.CancelAllAsync();
		await Wait.UntilAsync(() => queue.GetItems().All(item => item.Status == DownloadStatus.Canceled), "every download to be cancelled");
		await queue.CancelAllAsync();
		await queue.ClearFinishedAsync();

		Assert.Empty(queue.GetItems());
		Assert.Empty(queue.GetBatches());
		Assert.Null(queue.GetItem(single.Id));
	}

	[Fact]
	public async Task Dispose_CancelsActiveDownloads()
	{
		GatedContent blocked = new(book, sendFirst: 1000);
		handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = blocked };
		DownloadProgress queued = await queue.EnqueueAsync(Link(1), "Elric");
		await blocked.FirstChunkSent;

		queue.Dispose();

		await WaitForStatusAsync(queued.Id, DownloadStatus.Canceled);
	}

	static bool IsFree(string path)
	{
		try
		{
			using FileStream _ = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
			return true;
		}
		catch (IOException)
		{
			return false;
		}
	}

	static HttpResponseMessage Content(HttpStatusCode status, byte[] bytes) =>
		new(status) { Content = new ByteArrayContent(bytes) };

	static HttpResponseMessage RangeNotSatisfiable(long length) => new(HttpStatusCode.RequestedRangeNotSatisfiable)
	{
		Content = new ByteArrayContent([]) { Headers = { ContentRange = new ContentRangeHeaderValue(length) } },
	};

	sealed partial class ScriptedHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

		public List<HttpRequestMessage> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			lock (Requests)
			{
				Requests.Add(request);
			}

			return Task.FromResult(Respond(request));
		}
	}

	sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
	}

	sealed class FakeImporter : IBookImportService
	{
		public ConcurrentQueue<byte[]> Imported { get; } = new();

		public Exception? Failure { get; set; }

		public bool ReturnNothing { get; set; }

		public async Task<IReadOnlyList<BookSummary>> ImportLocalFileAsync(string filePath, IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default)
		{
			if (Failure is not null)
			{
				throw Failure;
			}

			Imported.Enqueue(await File.ReadAllBytesAsync(filePath, cancellationToken));
			return ReturnNothing
				? []
				: [new BookSummary(Path.GetFileName(filePath), "Title", "Author", "", "en", "", "", "", "", "", DateTimeOffset.UtcNow, null, "", 0, 1)];
		}

		public Task<IReadOnlyList<BookSummary>> ImportFileAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<IReadOnlyList<BookSummary>> ImportFolderAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	/// <summary>
	/// A response body that sends its first chunk, then holds the rest until released (or until the
	/// reader cancels), so a test can act on a download while it is mid-transfer.
	/// </summary>
	sealed partial class GatedContent(byte[] bytes, int sendFirst) : HttpContent
	{
		readonly TaskCompletionSource firstChunkSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
		readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task FirstChunkSent => firstChunkSent.Task;

		public void Release() => released.TrySetResult();

		protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
			SerializeToStreamAsync(stream, context, CancellationToken.None);

		protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
		{
			await stream.WriteAsync(bytes.AsMemory(0, sendFirst), cancellationToken);
			await stream.FlushAsync(cancellationToken);
			firstChunkSent.TrySetResult();
			await released.Task.WaitAsync(cancellationToken);
			await stream.WriteAsync(bytes.AsMemory(sendFirst), cancellationToken);
		}

		protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

		protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
		{
			System.IO.Pipelines.Pipe pipe = new();
			_ = Task.Run(async () =>
			{
				Stream writer = pipe.Writer.AsStream();
				try
				{
					await SerializeToStreamAsync(writer, null, CancellationToken.None);
				}
				finally
				{
					await pipe.Writer.CompleteAsync();
				}
			}, CancellationToken.None);
			return Task.FromResult(pipe.Reader.AsStream());
		}

		protected override bool TryComputeLength(out long length)
		{
			length = bytes.Length;
			return true;
		}
	}
}
