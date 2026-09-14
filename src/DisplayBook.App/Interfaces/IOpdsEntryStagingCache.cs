using DisplayBook.App.Models;

namespace DisplayBook.App.Interfaces;

/// <summary>
/// Stages an OPDS catalog entry tapped to open a book so the book details page can fall back
/// to entry metadata when the entry URL has no parseable detail document (Calibre content
/// servers expose only download links).
/// </summary>
public interface IOpdsEntryStagingCache
{
	void Stage(string entryUrl, OpdsEntry entry);

	/// <summary>Removes and returns the catalog entry previously staged for the given URL, if any.</summary>
	OpdsEntry? TakePendingEntry(string entryUrl);
}
