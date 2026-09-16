using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Xunit;

namespace DisplayBook.Tests;

/// <summary>
/// Release builds trim the app with reflection-based serialization disabled, so the OPDS feed
/// cache and server profiles have to serialize through <see cref="OpdsJsonContext"/>'s generated
/// metadata. These tests pin the generated output to the shape reflection produced, since both
/// files persist across upgrades and a silent naming/format change would orphan them.
/// </summary>
public class OpdsJsonContextTests
{
	// Mirrors OpdsCatalogCache.serializerOptions.
	static JsonSerializerOptions CacheOptions() => new()
	{
		WriteIndented = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		Converters = { new StringDictionaryConverter() },
	};

	// Mirrors OpdsServerRepository.serializerOptions.
	static JsonSerializerOptions ServerOptions() => new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	static JsonSerializerOptions WithReflection(JsonSerializerOptions options)
	{
		options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
		return options;
	}

	static OpdsFeed SampleFeed() => new()
	{
		Id = "urn:uuid:feed-1",
		Title = "Calibre Library",
		Subtitle = "By title",
		Updated = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
		Author = new Author { Name = "calibre", Uri = "http://calibre-ebook.com" },
		FeedType = FeedType.Acquisition,
		SourceUrl = "http://192.168.0.10:8080/opds/navcatalog/4e",
		Pagination = new PaginationInfo { HasNext = true, NextUrl = "http://192.168.0.10:8080/opds/next" },
		Links =
		{
			new Link { Href = "/opds", Rel = Link.RelSelf, Type = "application/atom+xml" },
		},
		ExtendedFeedMetadata = { ["totalResults"] = 128, ["searchTerms"] = "dune" },
		Entries =
		{
			new OpdsEntry
			{
				Id = "urn:uuid:entry-1",
				Title = "Dune",
				Published = new DateTime(1965, 8, 1, 0, 0, 0, DateTimeKind.Utc),
				Authors = { new Author { Name = "Frank Herbert" } },
				Summary = "Spice.",
				Cover = new OpdsCoverImage { Url = "/get/cover/1", ThumbnailUrl = "/get/thumb/1", Size = 4096 },
				Categories = { new OpdsCategory { Term = "Science Fiction", Label = "SF" } },
				Identifiers = { ["isbn13"] = "9780441013593" },
				ExtendedMetadata = { ["language"] = "eng", ["hasCover"] = true },
				Links =
				{
					new Link
					{
						Href = "/get/EPUB/1",
						Rel = Link.RelAcquisition,
						Type = "application/epub+zip",
						Length = "1048576",
						Properties = { ["opds:price"] = "0.00" },
					},
				},
			},
		},
	};

	[Fact]
	public void FeedSerializationMatchesReflectionOutput()
	{
		OpdsFeed feed = SampleFeed();

		string generated = JsonSerializer.Serialize(feed, new OpdsJsonContext(CacheOptions()).OpdsFeed);
		string reflected = JsonSerializer.Serialize(feed, WithReflection(CacheOptions()));

		Assert.Equal(reflected, generated);
	}

	[Fact]
	public void FeedRoundTripsThroughGeneratedMetadata()
	{
		OpdsJsonContext context = new(CacheOptions());
		string json = JsonSerializer.Serialize(SampleFeed(), context.OpdsFeed);

		OpdsFeed? restored = JsonSerializer.Deserialize(json, context.OpdsFeed);

		Assert.NotNull(restored);
		Assert.Equal("Calibre Library", restored.Title);
		Assert.Equal(FeedType.Acquisition, restored.FeedType);
		Assert.Equal("http://192.168.0.10:8080/opds/next", restored.Pagination?.NextUrl);
		Assert.Equal("calibre", restored.Author?.Name);

		OpdsEntry entry = Assert.Single(restored.Entries);
		Assert.Equal("Dune", entry.Title);
		Assert.Equal("Frank Herbert", Assert.Single(entry.Authors).Name);
		Assert.Equal("9780441013593", entry.Identifiers["isbn13"]);
		Assert.Equal("/get/cover/1", entry.Cover?.Url);
		Assert.Equal("eng", entry.ExtendedMetadata["language"]);

		// The custom converter reads every dictionary value back as string/bool/decimal.
		Assert.Equal(true, entry.ExtendedMetadata["hasCover"]);
		Assert.Equal(128m, restored.ExtendedFeedMetadata["totalResults"]);

		Link link = Assert.Single(entry.Links);
		Assert.Equal("/get/EPUB/1", link.Href);
		Assert.Equal("0.00", link.Properties["opds:price"]);
	}

	[Fact]
	public void CacheEntrySerializationMatchesReflectionOutput()
	{
		OpdsFeedCacheEntry entry = new()
		{
			CacheKey = "feed-0123456789abcdef.json",
			Url = "http://192.168.0.10:8080/opds",
			Title = "Calibre Library",
			ParentPath = "/",
			ServerId = "server-1",
			CachedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
			EntryCount = 3,
			SerializedFeed = """{"title":"Calibre Library"}""",
		};

		string generated = JsonSerializer.Serialize(entry, new OpdsJsonContext(CacheOptions()).OpdsFeedCacheEntry);
		string reflected = JsonSerializer.Serialize(entry, WithReflection(CacheOptions()));

		Assert.Equal(reflected, generated);

		OpdsFeedCacheEntry? restored = JsonSerializer.Deserialize(generated, new OpdsJsonContext(CacheOptions()).OpdsFeedCacheEntry);
		Assert.Equal("server-1", restored?.ServerId);
		Assert.Equal(3, restored?.EntryCount);
	}

	[Fact]
	public void ServerListSerializationMatchesReflectionOutput()
	{
		List<OpdsServer> servers =
		[
			new()
			{
				Id = "a1b2",
				Name = "Living room Calibre",
				Url = "http://192.168.0.10:8080/opds/",
				Type = ServerType.Discovered,
				LastSeen = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
				Username = "reader",
				Password = "hunter2",
				Metadata = { ["service"] = "calibre._tcp" },
			},
			new() { Id = "c3d4", Name = "Manual", Url = "http://example.test/opds" },
		];

		string generated = JsonSerializer.Serialize(servers, new OpdsJsonContext(ServerOptions()).ListOpdsServer);
		string reflected = JsonSerializer.Serialize(servers, WithReflection(ServerOptions()));

		Assert.Equal(reflected, generated);

		List<OpdsServer>? restored = JsonSerializer.Deserialize(generated, new OpdsJsonContext(ServerOptions()).ListOpdsServer);
		Assert.Equal(2, restored?.Count);
		Assert.Equal(ServerType.Discovered, restored?[0].Type);
		Assert.Equal("calibre._tcp", restored?[0].Metadata["service"]);
	}
}
