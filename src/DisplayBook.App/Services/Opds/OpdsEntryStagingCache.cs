using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services.Opds;

public sealed class OpdsEntryStagingCache : IOpdsEntryStagingCache
{
	readonly Dictionary<string, OpdsEntry> pendingEntries = new(StringComparer.OrdinalIgnoreCase);

	public void Stage(string entryUrl, OpdsEntry entry)
	{
		if (!string.IsNullOrWhiteSpace(entryUrl))
		{
			pendingEntries[entryUrl] = entry;
		}
	}

	public OpdsEntry? TakePendingEntry(string entryUrl)
	{
		return !string.IsNullOrWhiteSpace(entryUrl) && pendingEntries.Remove(entryUrl, out OpdsEntry? entry)
			? entry
			: null;
	}
}
