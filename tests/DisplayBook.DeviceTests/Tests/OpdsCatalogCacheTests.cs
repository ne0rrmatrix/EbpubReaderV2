using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests;

[Collection(AppDataCollection.Name)]
public sealed partial class OpdsCatalogCacheTests : IDisposable
{
	const string feedUrl = "http://calibre.local:8080/opds/navcatalog/4f";

	static readonly string cacheFolder = Path.Combine(BookStorageService.ContentRoot, "Cache");

	readonly OpdsCatalogCache cache = new();

	public OpdsCatalogCacheTests() => AppDataSandbox.Reset();

	public void Dispose() => cache.Dispose();

	static OpdsFeed MakeFeed(string title) => new()
	{
		Title = title,
		Entries =
		[
			new OpdsEntry { Id = "1", Title = "Elric of Melniboné" },
			new OpdsEntry { Id = "2", Title = "The Sailor on the Seas of Fate" },
		],
	};

	[Fact]
	public async Task Get_NothingCached_ReturnsNull()
	{
		Assert.Null(await cache.GetAsync(feedUrl));
	}

	[Fact]
	public async Task Set_ThenGet_RoundTripsTheFeed()
	{
		await cache.SetAsync(MakeFeed("By Author"), feedUrl);

		OpdsFeed? cached = await cache.GetAsync(feedUrl);

		Assert.NotNull(cached);
		Assert.Equal("By Author", cached.Title);
		Assert.Equal(["Elric of Melniboné", "The Sailor on the Seas of Fate"], cached.Entries.Select(entry => entry.Title));
	}

	[Fact]
	public async Task Get_TrailingSlashVariant_FindsTheSameEntry()
	{
		await cache.SetAsync(MakeFeed("By Author"), feedUrl);

		Assert.Equal("By Author", (await cache.GetAsync(feedUrl + "/"))?.Title);
	}

	[Fact]
	public async Task Get_OtherPath_IsAMiss()
	{
		await cache.SetAsync(MakeFeed("By Author"), feedUrl);

		Assert.Null(await cache.GetAsync("http://calibre.local:8080/opds/navcatalog/4e"));
	}

	[Fact]
	public async Task Get_OlderThanMaxAge_ReturnsNull()
	{
		await cache.SetAsync(MakeFeed("By Author"), feedUrl);
		await Task.Delay(50);

		Assert.Null(await cache.GetAsync(feedUrl, maxAge: TimeSpan.FromMilliseconds(1)));
		Assert.NotNull(await cache.GetAsync(feedUrl, maxAge: TimeSpan.FromHours(1)));
	}

	[Fact]
	public async Task Remove_DeletesOnlyThatFeed()
	{
		const string otherUrl = "http://calibre.local:8080/opds/navcatalog/4e";
		await cache.SetAsync(MakeFeed("By Author"), feedUrl);
		await cache.SetAsync(MakeFeed("By Series"), otherUrl);

		await cache.RemoveAsync(feedUrl);

		Assert.Null(await cache.GetAsync(feedUrl));
		Assert.NotNull(await cache.GetAsync(otherUrl));
	}

	[Fact]
	public async Task Clear_DeletesEveryFeed()
	{
		await cache.SetAsync(MakeFeed("By Author"), feedUrl);

		await cache.ClearAsync();

		Assert.Null(await cache.GetAsync(feedUrl));
		Assert.False(Directory.Exists(cacheFolder));
	}

	[Fact]
	public async Task Get_CorruptEntry_ReturnsNull()
	{
		await cache.SetAsync(MakeFeed("By Author"), feedUrl);
		string file = Assert.Single(Directory.GetFiles(cacheFolder));
		await File.WriteAllTextAsync(file, "{ not json");

		Assert.Null(await cache.GetAsync(feedUrl));
	}

	[Fact]
	public async Task BlankUrl_IsNeverCached()
	{
		await cache.SetAsync(MakeFeed("Nowhere"), "  ");

		Assert.Null(await cache.GetAsync("  "));
		Assert.False(Directory.Exists(cacheFolder));
	}
}
