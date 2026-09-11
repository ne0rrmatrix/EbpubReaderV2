namespace DisplayBook.App.Models;

/// <summary>
/// The kind of OPDS feed: navigation catalogs, acquisition (book) lists, or search results.
/// </summary>
public enum FeedType
{
	Navigation,
	Acquisition,
	Search,
	Unknown
}

public enum ServerType
{
	Manual,
	Discovered
}

public sealed class OpdsFeed
{
	public string Id { get; set; } = string.Empty;

	public string Title { get; set; } = string.Empty;

	public string Subtitle { get; set; } = string.Empty;

	public DateTime? Updated { get; set; }

	public Author? Author { get; set; }

	public FeedType FeedType { get; set; } = FeedType.Unknown;

	public List<OpdsEntry> Entries { get; set; } = [];

	public PaginationInfo? Pagination { get; set; }

	/// <summary>
	/// Feed-level links (self, next, prev, search, up).
	/// </summary>
	public List<Link> Links { get; set; } = [];

	/// <summary>
	/// Feed-level metadata such as totalResults / totalPages when supplied by the server.
	/// </summary>
	public Dictionary<string, object> ExtendedFeedMetadata { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// Absolute URL the feed was retrieved from, used to resolve relative link targets.
	/// </summary>
	public string? SourceUrl { get; set; }

	public Link? GetSelfLink()
	{
		return Links.FirstOrDefault(link => string.Equals(link.Rel, Link.RelSelf, StringComparison.OrdinalIgnoreCase));
	}

	public Link? GetNextLink()
	{
		return Links.FirstOrDefault(link => string.Equals(link.Rel, Link.RelNext, StringComparison.OrdinalIgnoreCase));
	}

	public Link? GetPrevLink()
	{
		return Links.FirstOrDefault(link => string.Equals(link.Rel, Link.RelPrev, StringComparison.OrdinalIgnoreCase));
	}

	public Link? GetUpLink()
	{
		return Links.FirstOrDefault(link => string.Equals(link.Rel, Link.RelUp, StringComparison.OrdinalIgnoreCase));
	}

	public Link? GetSearchLink()
	{
		return Links.FirstOrDefault(link => string.Equals(link.Rel, Link.RelSearch, StringComparison.OrdinalIgnoreCase));
	}

	public Link? GetFirstLink()
	{
		return Links.FirstOrDefault(link => string.Equals(link.Rel, Link.RelFirst, StringComparison.OrdinalIgnoreCase));
	}

	public Link? GetLastLink()
	{
		return Links.FirstOrDefault(link => string.Equals(link.Rel, Link.RelLast, StringComparison.OrdinalIgnoreCase));
	}
}

public sealed class Author
{
	public string Name { get; set; } = string.Empty;

	public string? Uri { get; set; }
}

public sealed class OpdsCategory
{
	public string Term { get; set; } = string.Empty;

	public string? Label { get; set; }

	public string? Scheme { get; set; }
}

public sealed class OpdsCoverImage
{
	public string Url { get; set; } = string.Empty;

	public string? ThumbnailUrl { get; set; }

	public string? Format { get; set; }

	public long? Size { get; set; }

	public int? Order { get; set; }
}

public sealed class OpdsEntry
{
	public string Id { get; set; } = string.Empty;

	public string Title { get; set; } = string.Empty;

	public DateTime? Updated { get; set; }

	public DateTime? Published { get; set; }

	public List<Author> Authors { get; set; } = [];

	public string? Summary { get; set; }

	/// <summary>
	/// HTML or plain-text entry body when the feed supplies richer content than the summary.
	/// </summary>
	public string? Content { get; set; }

	public List<Link> Links { get; set; } = [];

	public List<OpdsCategory> Categories { get; set; } = [];

	public OpdsCoverImage? Cover { get; set; }

	/// <summary>
	/// Series name when the feed carries OPDS 1.1 series metadata.
	/// </summary>
	public string? Series { get; set; }

	/// <summary>
	/// ISBN or other identifiers keyed by kind (e.g. "isbn13").
	/// </summary>
	public Dictionary<string, string> Identifiers { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// Additional parsed metadata (language, publisher, price, etc.).
	/// </summary>
	public Dictionary<string, object> ExtendedMetadata { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];
}

public sealed class Link
{
	public const string RelSelf = "self";
	public const string RelNext = "next";
	public const string RelPrev = "prev";
	public const string RelFirst = "first";
	public const string RelLast = "last";
	public const string RelUp = "up";
	public const string RelSearch = "search";

	/// <summary>
	/// OPDS 1.0/1.1 acquisition relation (opds-spec.org namespace identifier, not a URL target).
	/// </summary>
	public const string RelAcquisition = "http://opds-spec.org/acquisition";
	public const string RelSale = "http://opds-spec.org/sale";
	public const string RelSample = "http://opds-spec.org/sample";

	public string Href { get; set; } = string.Empty;

	public string? Rel { get; set; }

	public string? Type { get; set; }

	public string? Title { get; set; }

	public string? Length { get; set; }

	public string? Language { get; set; }

	/// <summary>
	/// Link properties (e.g. "opds:color", "opds:price").
	/// </summary>
	public Dictionary<string, string> Properties { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];

	public bool IsAcquisition()
	{
		return string.Equals(Rel, RelSelf, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(Rel, RelAcquisition, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(Rel, RelSale, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(Rel, RelSample, StringComparison.OrdinalIgnoreCase) ||
			string.IsNullOrWhiteSpace(Rel);
	}

	public bool IsNavigation()
	{
		return Rel is not null &&
			!string.IsNullOrWhiteSpace(Rel) &&
			!IsAcquisition();
	}
}

public sealed class PaginationInfo
{
	public bool HasPrevious { get; set; }

	public bool HasNext { get; set; }

	public string? PreviousUrl { get; set; }

	public string? NextUrl { get; set; }
}

public sealed class BookDetails
{
	public string Id { get; set; } = string.Empty;

	public string Title { get; set; } = string.Empty;

	public string Subtitle { get; set; } = string.Empty;

	public List<Author> Authors { get; set; } = [];

	public string? Publisher { get; set; }

	public DateTime? Published { get; set; }

	public DateTime? Updated { get; set; }

	public string? Language { get; set; }

	public string? Description { get; set; }

	public string? Summary { get; set; }

	public List<OpdsCategory> Categories { get; set; } = [];

	public OpdsCoverImage? Cover { get; set; }

	public string? Series { get; set; }

	public string? Issn { get; set; }

	public Dictionary<string, string> Identifiers { get; set; } = [with(StringComparer.OrdinalIgnoreCase)];

	public List<DownloadLink> DownloadLinks { get; set; } = [];

	/// <summary>
	/// URL the details document was retrieved from.
	/// </summary>
	public string? SourceUrl { get; set; }
}
