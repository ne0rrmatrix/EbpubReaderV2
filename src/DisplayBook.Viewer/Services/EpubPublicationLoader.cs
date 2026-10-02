using System.Runtime.CompilerServices;
using DisplayBook.Viewer.Models;

namespace DisplayBook.Viewer.Services;

/// <summary>
/// Turns an in-memory <see cref="EpubArchive"/> into everything the reader needs to display it:
/// the parsed OPF/spine/TOC (<see cref="EpubPublicationParser"/>) and the single combined reading
/// document (<see cref="CombinedDocumentBuilder"/>), stored back into the archive as a synthetic
/// entry so every platform's resource handler serves it like any other file.
///
/// Memoized per archive instance so this work happens exactly once per opened book no matter how
/// many callers ask for it. That's what lets the App project start it early -- while the user is
/// still on the details page -- and have <c>EpubReaderView</c> later await an already-finished
/// result instead of redoing it. <see cref="EpubArchive.SetSyntheticEntry"/> is internal to this
/// assembly, so the App project could not drive this sequence itself; this is the seam that lets
/// it ask for the work without owning the steps.
/// </summary>
public static class EpubPublicationLoader
{
	// Keyed by archive instance (rather than by book id) so the memoized result is inseparable
	// from the archive it mutated -- a second archive opened for the same book is a genuinely
	// different object and needs its own synthetic combined-document entry. Weak keys mean
	// nothing is retained once a book's archive is dropped.
	static readonly ConditionalWeakTable<EpubArchive, Task<EpubPublicationInfo>> preparedByArchive = new();

	// ConditionalWeakTable.GetValue's factory can run more than once under a race (only one
	// result is kept), which here would mean parsing and assembling a large book twice for
	// nothing. The prefetch and the reader genuinely can call this concurrently -- a fast tap
	// straight through the details page -- so the factory runs under this lock instead.
	static readonly Lock gate = new();

	/// <summary>
	/// Parses <paramref name="archive"/> and builds its combined reading document, or returns the
	/// in-flight/completed result if that has already been started for this archive. The work
	/// itself is deliberately not cancellable -- it is shared between callers, so one caller
	/// walking away must not cancel it for another. Callers that need to stop waiting should
	/// apply their own token to the returned task with <c>WaitAsync</c> instead.
	/// </summary>
	public static Task<EpubPublicationInfo> PrepareAsync(EpubArchive archive)
	{
		ArgumentNullException.ThrowIfNull(archive);

		lock (gate)
		{
			if (preparedByArchive.TryGetValue(archive, out Task<EpubPublicationInfo>? existing))
			{
				return existing;
			}

			// Parsing the OPF/spine/TOC and assembling every chapter into one document is pure
			// CPU work -- the archive is already fully in memory -- but it is real work for a
			// large book, so it goes to the thread pool rather than running on whichever thread
			// asked for it first (which, on the reader path, is the UI thread).
			Task<EpubPublicationInfo> task = Task.Run(() => Prepare(archive));
			preparedByArchive.Add(archive, task);
			return task;
		}
	}

	static EpubPublicationInfo Prepare(EpubArchive archive)
	{
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);
		byte[] combinedDocument = CombinedDocumentBuilder.Build(archive, publication);
		archive.SetSyntheticEntry(CombinedDocumentBuilder.CombinedDocumentPath, combinedDocument);
		return publication;
	}
}
