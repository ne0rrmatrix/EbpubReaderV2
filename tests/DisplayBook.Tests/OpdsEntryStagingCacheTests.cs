using DisplayBook.App.Models;
using DisplayBook.App.Services.Opds;
using Xunit;

namespace DisplayBook.Tests;

public class OpdsEntryStagingCacheTests
{
	const string entryUrl = "http://calibre.local:8080/opds/book/3819";

	[Fact]
	public void TakePendingEntry_ReturnsStagedEntry()
	{
		OpdsEntryStagingCache cache = new();
		OpdsEntry entry = new() { Id = "3819", Title = "Elric" };

		cache.Stage(entryUrl, entry);

		Assert.Same(entry, cache.TakePendingEntry(entryUrl));
	}

	[Fact]
	public void TakePendingEntry_RemovesEntrySoSecondTakeReturnsNull()
	{
		OpdsEntryStagingCache cache = new();
		cache.Stage(entryUrl, new OpdsEntry());

		cache.TakePendingEntry(entryUrl);

		Assert.Null(cache.TakePendingEntry(entryUrl));
	}

	[Fact]
	public void TakePendingEntry_UrlMatchIsCaseInsensitive()
	{
		OpdsEntryStagingCache cache = new();
		OpdsEntry entry = new();
		cache.Stage(entryUrl, entry);

		Assert.Same(entry, cache.TakePendingEntry(entryUrl.ToUpperInvariant()));
	}

	[Fact]
	public void Stage_SameUrlTwice_LaterEntryWins()
	{
		OpdsEntryStagingCache cache = new();
		OpdsEntry first = new() { Title = "First" };
		OpdsEntry second = new() { Title = "Second" };

		cache.Stage(entryUrl, first);
		cache.Stage(entryUrl, second);

		Assert.Same(second, cache.TakePendingEntry(entryUrl));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Stage_BlankUrl_IsIgnored(string blankUrl)
	{
		OpdsEntryStagingCache cache = new();

		cache.Stage(blankUrl, new OpdsEntry());

		Assert.Null(cache.TakePendingEntry(blankUrl));
	}

	[Fact]
	public void TakePendingEntry_UnknownUrl_ReturnsNull()
	{
		OpdsEntryStagingCache cache = new();
		cache.Stage(entryUrl, new OpdsEntry());

		Assert.Null(cache.TakePendingEntry("http://calibre.local:8080/opds/book/1"));
	}
}
