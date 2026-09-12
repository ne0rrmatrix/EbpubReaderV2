namespace DisplayBook.App.Interfaces;

public sealed record RemoteReadingPosition(string ResourceHref, int CharOffset, int Page, int PageCount, DateTimeOffset UpdatedAt);

/// <summary>
/// Syncs reading position across devices via the Firestore REST API, keyed by a
/// book's <c>ContentHash</c> (stable across devices for a byte-identical EPUB,
/// unlike the locally-generated book Id). No-ops entirely when signed out.
/// </summary>
public interface IPositionSyncService
{
	/// <summary>
	/// Queues a push of the given position, debounced so rapid page turns/scrolls
	/// (the reader reports a position on nearly every one) don't each trigger a
	/// network write. Call <see cref="FlushPendingPushAsync"/> to send immediately.
	/// </summary>
	void SchedulePush(string contentHash, string resourceHref, int charOffset, int page, int pageCount, DateTimeOffset updatedAtUtc);

	/// <summary>Sends any pending debounced push immediately (e.g. when leaving the reader).</summary>
	Task FlushPendingPushAsync();

	Task<RemoteReadingPosition?> TryPullPositionAsync(string contentHash, CancellationToken cancellationToken = default);
}