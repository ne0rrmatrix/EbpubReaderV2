using System.Globalization;
using System.Text;
using System.Xml.Linq;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public interface IOpdsParserService
{
	/// <summary>
	/// Downloads and parses an OPDS/Atom feed.
	/// </summary>
	Task<OpdsFeed> ParseFeedAsync(string feedUrl, CancellationToken cancellationToken = default);

	/// <summary>
	/// Parses an OPDS/Atom feed from a stream (Atom/OPDS 1.0/1.1 and OPDS 2.0 RDF).
	/// </summary>
	Task<OpdsFeed> ParseFeedAsync(Stream feedStream, CancellationToken cancellationToken = default);

	/// <summary>
	/// Parses a single book details document (Atom entry or OPDS 2.0 book) and collects its acquisition links.
	/// </summary>
	Task<BookDetails> ParseBookDetailsAsync(string entryUrl, CancellationToken cancellationToken = default);

	/// <summary>
	/// Builds book details directly from a catalog entry, used when the entry has no separate detail document
	/// (for example Calibre content servers only expose download links).
	/// </summary>
	Task<BookDetails> ParseBookDetailsAsync(OpdsEntry entry, CancellationToken cancellationToken = default);

	/// <summary>
	/// Resolves all acquisition (download) links in a document.
	/// </summary>
	Task<IEnumerable<DownloadLink>> GetDownloadLinksAsync(string acquisitionUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// A lightweight, stateless OPDS/Atom parser. Matching is done on element local names so the
/// parser tolerates documents with, without, or with non-standard XML namespaces (Calibre content
/// servers, OPDS 1.0/1.1 catalogs, and OPDS 2.0 RDF).
/// </summary>
public sealed class OpdsParserService(HttpClient httpClient) : IOpdsParserService
{
	// OPDS 1.0/1.1 media types and relations (opds-spec.org namespace identifiers, not network requests).
	const string opdsImageRel = "http://opds-spec.org/image";
	const string opdsImageThumbRel = "http://opds-spec.org/image-thumbnail";
	const string opdsThumbnailRel = "http://opds-spec.org/thumbnail";
	const string opdsAcquisitionFeedType = "http://opds-spec.org/acquisition";
	const string opdsAcquisitionFeedEntryType = "http://opds-spec.org/acquisition-feed";
	const string opdsNavigationFeedType = "http://opds-spec.org/navigation";
	const string opdsNavigationFeedEntryType = "http://opds-spec.org/navigation-feed";
	const string opdsSearchFeedType = "http://opds-spec.org/search-feed";

	const string feedTypeValueNavigation = "navigation";
	const string feedTypeValueAcquisition = "acquisition";
	const string feedTypeValueSearch = "search";

	const string defaultTitle = "Untitled";
	const string defaultCatalogTitle = "OPDS catalog";
	const string blankUri = "about:blank";
	const string seriesScheme = "series";
	const string contentTypeMetadataKey = "contentType";
	const string idKindDefault = "identifier";
	const string guidFormat = "N";

	static readonly string[] acquisitionTypes =
	[
		Mime.Epub,
		Mime.Epub3,
		Mime.Pdf,
		Mime.Mobi,
		Mime.Mobipocket,
		Mime.Mobi10,
		Mime.Azw3,
		Mime.Audiobook,
		Mime.Rtf,
		Mime.PlainText,
		Mime.Palm,
		Mime.Fb2,
		Mime.Lit
	];

	public async Task<OpdsFeed> ParseFeedAsync(string feedUrl, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(feedUrl);

		using HttpResponseMessage response = await httpClient.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		byte[] content = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);

		OpdsFeed? feed = await Task.Run(() => ParseFeedFromBytes(content, feedUrl), cancellationToken).ConfigureAwait(false);
		return feed ?? throw new OpdsFeedException($"The document at '{feedUrl}' is not a valid OPDS feed.");
	}

	public Task<OpdsFeed> ParseFeedAsync(Stream feedStream, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(feedStream);
		return Task.Run(() =>
		{
			using StreamReader reader = new(feedStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
			string text = reader.ReadToEnd();
			return ParseFeedFromBytes(Encoding.UTF8.GetBytes(text), sourceUrl: null)
				?? throw new OpdsFeedException("The provided stream is not a valid OPDS feed.");
		}, cancellationToken);
	}

	public async Task<BookDetails> ParseBookDetailsAsync(string entryUrl, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(entryUrl);

		using HttpResponseMessage response = await httpClient.GetAsync(entryUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		byte[] content = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);

		BookDetails? details = await Task.Run(() => ParseBookDetailsFromBytes(content, entryUrl), cancellationToken).ConfigureAwait(false);
		if (details is not null)
		{
			return details;
		}

		// Some servers serve an acquisition feed for an entry URL; fall back to the first entry.
		OpdsFeed? feed = await Task.Run(() => ParseFeedFromBytes(content, entryUrl), cancellationToken).ConfigureAwait(false);
		return feed is not null && feed.Entries.Count > 0
			? ToBookDetails(feed.Entries[0], entryUrl)
			: throw new OpdsFeedException($"The document at '{entryUrl}' is not a valid OPDS book document.");
	}

	public Task<BookDetails> ParseBookDetailsAsync(OpdsEntry entry, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(entry);
		return Task.Run(() => BuildDetailsFromEntry(entry), cancellationToken);
	}

	public async Task<IEnumerable<DownloadLink>> GetDownloadLinksAsync(string acquisitionUrl, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(acquisitionUrl);

		using HttpResponseMessage response = await httpClient.GetAsync(acquisitionUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		byte[] content = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);

		return await Task.Run(() => GetDownloadLinksFromBytes(content, acquisitionUrl), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Pure, synchronous parse from bytes. Returns <see langword="null"/> when the document is not a recognized feed.
	/// </summary>
	public static OpdsFeed? ParseFeedFromBytes(byte[] content, string? sourceUrl)
	{
		XElement? root = LoadRoot(content);
		return root is null
			? null
			: root.Name.LocalName switch
			{
				Tags.Feed => ParseAtomFeed(root, sourceUrl),
				Tags.Entry => BuildSingleEntryFeed(ParseAtomEntry(root, sourceUrl), sourceUrl),
				Tags.Rdf => ParseOpds20Rdf(root, sourceUrl),
				_ => null
			};
	}

	static List<DownloadLink> GetDownloadLinksFromBytes(byte[] content, string? sourceUrl)
	{
		BookDetails? details = ParseBookDetailsFromBytes(content, sourceUrl);
		if (details is not null)
		{
			return details.DownloadLinks;
		}

		OpdsFeed? feed = ParseFeedFromBytes(content, sourceUrl);
		if (feed is null)
		{
			return [];
		}

		List<DownloadLink> links = new();
		foreach (OpdsEntry entry in feed.Entries)
		{
			foreach (Link? link in entry.Links.Where(LinkIsAcquisition))
			{
				links.Add(ToDownloadLink(link));
			}
		}

		return links;
	}

	static DownloadLink ToDownloadLink(Link link)
	{
		return new DownloadLink
		{
			Url = link.Href,
			Format = link.Type,
			FormatName = FormatNameFor(link.Type),
			Size = ParseLength(link.Length),
			Title = link.Title,
			Properties = new Dictionary<string, string>(link.Properties, StringComparer.OrdinalIgnoreCase)
		};
	}

	static OpdsFeed BuildSingleEntryFeed(OpdsEntry entry, string? sourceUrl)
	{
		return new OpdsFeed
		{
			Title = entry.Title,
			Id = entry.Id,
			SourceUrl = sourceUrl,
			FeedType = FeedType.Unknown,
			Entries = [entry]
		};
	}

	static OpdsFeed ParseAtomFeed(XElement root, string? sourceUrl)
	{
		OpdsFeed feed = CreateFeed(root, sourceUrl);
		ParseFeedLinks(feed, root, sourceUrl);
		ParseFeedMetadata(feed, root);

		foreach (XElement entryEl in Locals(root, Tags.Entry))
		{
			feed.Entries.Add(ParseAtomEntry(entryEl, sourceUrl));
		}

		return feed;
	}

	static OpdsFeed CreateFeed(XElement root, string? sourceUrl)
	{
		return new OpdsFeed
		{
			Id = FirstLocal(root, Tags.Id)?.Value ?? string.Empty,
			Title = FirstLocal(root, Tags.Title)?.Value ?? defaultCatalogTitle,
			Subtitle = FirstLocal(root, Tags.Subtitle)?.Value ?? string.Empty,
			Updated = ParseDate(FirstLocal(root, Tags.Updated)?.Value),
			Author = ParseAuthor(FirstLocal(root, Tags.Author)),
			FeedType = DetectFeedType(root),
			SourceUrl = sourceUrl
		};
	}

	static void ParseFeedLinks(OpdsFeed feed, XElement root, string? sourceUrl)
	{
		foreach (XElement linkEl in Locals(root, Tags.Link))
		{
			feed.Links.Add(ParseLinkElement(linkEl, sourceUrl));
		}

		Link? next = feed.GetNextLink();
		Link? prev = feed.GetPrevLink();
		feed.Pagination = new PaginationInfo
		{
			HasNext = next is not null,
			HasPrevious = prev is not null,
			NextUrl = next?.Href,
			PreviousUrl = prev?.Href
		};
	}

	static void ParseFeedMetadata(OpdsFeed feed, XElement root)
	{
		AddFeedMetadata(feed, root, Tags.TotalResults);
		AddFeedMetadata(feed, root, Tags.TotalPages);
		AddFeedMetadata(feed, root, Tags.Page);
	}

	static void AddFeedMetadata(OpdsFeed feed, XElement root, string tag)
	{
		if (FirstLocal(root, tag)?.Value is { } value)
		{
			feed.ExtendedFeedMetadata[tag] = value;
		}
	}

	static OpdsEntry ParseAtomEntry(XElement entryEl, string? sourceUrl)
	{
		OpdsEntry entry = CreateEntry(entryEl);
		ParseEntryTextFields(entry, entryEl);
		ParseEntryPeopleAndTags(entry, entryEl);
		ParseEntryLinks(entry, entryEl, sourceUrl);
		return entry;
	}

	static OpdsEntry CreateEntry(XElement entryEl)
	{
		return new OpdsEntry
		{
			Id = FirstLocal(entryEl, Tags.Id)?.Value ?? NewId(),
			Title = FirstLocal(entryEl, Tags.Title)?.Value ?? defaultTitle
		};
	}

	static void ParseEntryTextFields(OpdsEntry entry, XElement entryEl)
	{
		entry.Updated = ParseDate(FirstLocal(entryEl, Tags.Updated)?.Value);
		entry.Published = ParseDate(FirstLocal(entryEl, Tags.Published)?.Value);
		entry.Summary = FirstLocal(entryEl, Tags.Summary)?.Value;

		XElement? contentEl = FirstLocal(entryEl, Tags.Content);
		entry.Content = contentEl?.Value;
		if (contentEl?.Attribute(Attr.Type)?.Value is { } contentType)
		{
			entry.ExtendedMetadata[contentTypeMetadataKey] = contentType;
		}

		entry.Series = FirstLocal(entryEl, Tags.Series)?.Value;
		if (!string.IsNullOrWhiteSpace(entry.Series))
		{
			entry.Categories.Add(new OpdsCategory { Term = entry.Series, Label = entry.Series, Scheme = seriesScheme });
		}

		if (FirstLocal(entryEl, Tags.Language)?.Value is { } language)
		{
			entry.ExtendedMetadata[Tags.Language] = language;
		}

		if (FirstLocal(entryEl, Tags.Publisher)?.Value is { } publisher)
		{
			entry.ExtendedMetadata[Tags.Publisher] = publisher;
		}

		foreach (XElement idEl in Locals(entryEl, Tags.Identifier))
		{
			entry.Identifiers[idEl.Attribute(Attr.Kind)?.Value ?? idKindDefault] = idEl.Value;
		}
	}

	static void ParseEntryPeopleAndTags(OpdsEntry entry, XElement entryEl)
	{
		foreach (XElement authorEl in Locals(entryEl, Tags.Author))
		{
			if (ParseAuthor(authorEl) is { } author)
			{
				entry.Authors.Add(author);
			}
		}

		foreach (XElement catEl in Locals(entryEl, Tags.Category))
		{
			if (ParseCategory(catEl) is { } category)
			{
				entry.Categories.Add(category);
			}
		}
	}

	static OpdsCategory? ParseCategory(XElement catEl)
	{
		string? term = catEl.Attribute(Attr.Term)?.Value;
		return string.IsNullOrWhiteSpace(term)
			? null
			: new OpdsCategory
			{
				Term = term,
				Label = catEl.Attribute(Attr.Label)?.Value,
				Scheme = catEl.Attribute(Attr.Scheme)?.Value ?? catEl.Attribute(Attr.Authority)?.Value
			};
	}

	static void ParseEntryLinks(OpdsEntry entry, XElement entryEl, string? sourceUrl)
	{
		foreach (XElement linkEl in Locals(entryEl, Tags.Link))
		{
			string? rel = linkEl.Attribute(Attr.Rel)?.Value;
			if (!IsImageLink(rel, linkEl.Attribute(Attr.Type)?.Value))
			{
				entry.Links.Add(ParseLinkElement(linkEl, sourceUrl));
				continue;
			}

			bool isThumbnail = string.Equals(rel, opdsImageThumbRel, StringComparison.OrdinalIgnoreCase) ||
				string.Equals(rel, opdsThumbnailRel, StringComparison.OrdinalIgnoreCase);
			ApplyCover(entry, CreateCoverImage(linkEl, sourceUrl), isThumbnail);
		}
	}

	static OpdsCoverImage CreateCoverImage(XElement linkEl, string? sourceUrl)
	{
		return new OpdsCoverImage
		{
			Url = ResolveUri(linkEl, sourceUrl),
			Format = linkEl.Attribute(Attr.Type)?.Value,
			Size = ParseLength(linkEl.Attribute(Attr.Length)?.Value)
		};
	}

	static void ApplyCover(OpdsEntry entry, OpdsCoverImage image, bool isThumbnail)
	{
		if (entry.Cover is null)
		{
			entry.Cover = image;
			if (isThumbnail)
			{
				entry.Cover.ThumbnailUrl = image.Url;
			}
		}
		else if (isThumbnail)
		{
			entry.Cover.ThumbnailUrl ??= image.Url;
		}
		else
		{
			// Prefer a full-size (non-thumbnail) image as the primary cover, keeping any known thumbnail.
			image.ThumbnailUrl = entry.Cover.ThumbnailUrl;
			entry.Cover = image;
		}
	}

	static OpdsFeed? ParseOpds20Rdf(XElement root, string? sourceUrl)
	{
		List<XElement> descriptions = GetRdfDescriptions(root);
		if (descriptions.Count == 0)
		{
			return null;
		}

		OpdsFeed feed = new()
		{
			Title = FirstLocal(root, Tags.Title)?.Value ?? defaultCatalogTitle,
			SourceUrl = sourceUrl
		};

		foreach (XElement desc in descriptions)
		{
			feed.Entries.Add(CreateRdfEntry(desc, sourceUrl));
		}

		feed.FeedType = feed.Entries.All(e => e.Links.Any(LinkIsAcquisition))
			? FeedType.Acquisition
			: FeedType.Unknown;

		return feed;
	}

	static List<XElement> GetRdfDescriptions(XElement root)
	{
		// RDF book/collection containers may be written with different casing (e.g. <Description>),
		// so match the description container case-insensitively.
		List<XElement> descriptions = root.Elements()
			.Where(e => string.Equals(e.Name.LocalName, Tags.Description, StringComparison.OrdinalIgnoreCase))
			.ToList();
		descriptions.AddRange(Locals(root, Tags.Book));
		descriptions.AddRange(Locals(root, Tags.Collection));
		return descriptions;
	}

	static OpdsEntry CreateRdfEntry(XElement desc, string? sourceUrl)
	{
		OpdsEntry entry = new()
		{
			Id = desc.Attribute(Attr.Id)?.Value ?? FirstLocal(desc, Tags.Identifier)?.Value ?? NewId(),
			Title = FirstLocal(desc, Tags.Name)?.Value ?? FirstLocal(desc, Tags.Title)?.Value ?? defaultTitle,
			Summary = FirstLocal(desc, Tags.Description)?.Value ?? FirstLocal(desc, Tags.Summary)?.Value,
			Published = ParseDate(FirstLocal(desc, Tags.DatePublished)?.Value ?? FirstLocal(desc, Tags.DateCreated)?.Value)
		};

		foreach (XElement? authorEl in Locals(desc, Tags.Author).Concat(Locals(desc, Tags.Creator)))
		{
			string name = FirstLocal(authorEl, Tags.Name)?.Value ?? authorEl.Value;
			if (!string.IsNullOrWhiteSpace(name))
			{
				entry.Authors.Add(new Author { Name = name });
			}
		}

		foreach (XElement? linkEl in Locals(desc, Tags.Url).Concat(Locals(desc, Tags.Image)))
		{
			bool isImage = string.Equals(linkEl.Name.LocalName, Tags.Image, StringComparison.Ordinal);
			string? contentType = linkEl.Attribute(Attr.ContentType)?.Value ?? linkEl.Element(Tags.Content)?.Value;
			string href = ResolveUriRaw(linkEl.Value, sourceUrl);
			if (isImage)
			{
				entry.Cover = new OpdsCoverImage { Url = href, Format = contentType };
			}
			else
			{
				entry.Links.Add(new Link { Href = href, Rel = Link.RelSelf, Type = contentType, Title = entry.Title });
			}
		}

		if ((FirstLocal(desc, Tags.InLanguage) ?? FirstLocal(desc, Tags.Language))?.Value is { } language)
		{
			entry.ExtendedMetadata[Tags.Language] = language;
		}

		if ((FirstLocal(desc, Tags.Provider) ?? FirstLocal(desc, Tags.Publisher))?.Value is { } publisher)
		{
			entry.ExtendedMetadata[Tags.Publisher] = publisher;
		}

		return entry;
	}

	static BookDetails? ParseBookDetailsFromBytes(byte[] content, string? sourceUrl)
	{
		XElement? root = LoadRoot(content);
		return root is null
			? null
			: root.Name.LocalName switch
			{
				Tags.Entry => ToBookDetails(ParseAtomEntry(root, sourceUrl), sourceUrl),
				Tags.Rdf => FirstRdfEntry(root, sourceUrl) is { } rdf ? ToBookDetails(rdf, sourceUrl) : null,
				Tags.Feed => FirstFeedEntry(root, sourceUrl) is { } feed ? ToBookDetails(feed, sourceUrl) : null,
				_ => null
			};
	}

	static OpdsEntry? FirstRdfEntry(XElement root, string? sourceUrl)
	{
		OpdsFeed? feed = ParseOpds20Rdf(root, sourceUrl);
		return feed is null || feed.Entries.Count == 0 ? null : feed.Entries[0];
	}

	static OpdsEntry? FirstFeedEntry(XElement root, string? sourceUrl)
	{
		OpdsFeed feed = ParseAtomFeed(root, sourceUrl);
		return feed.Entries.Count == 0 ? null : feed.Entries[0];
	}

	static BookDetails ToBookDetails(OpdsEntry entry, string? sourceUrl)
	{
		BookDetails details = new()
		{
			Id = entry.Id,
			Title = entry.Title,
			Authors = entry.Authors,
			Summary = entry.Summary,
			Description = entry.Content ?? entry.Summary,
			Published = entry.Published,
			Updated = entry.Updated,
			Cover = entry.Cover,
			Categories = entry.Categories,
			Series = entry.Series,
			Identifiers = entry.Identifiers,
			SourceUrl = sourceUrl
		};

		if (entry.ExtendedMetadata.TryGetValue(Tags.Language, out object? language))
		{
			details.Language = language?.ToString();
		}

		if (entry.ExtendedMetadata.TryGetValue(Tags.Publisher, out object? publisher))
		{
			details.Publisher = publisher?.ToString();
		}

		details.DownloadLinks = entry.Links.Where(LinkIsAcquisition).Select(ToDownloadLink).ToList();
		return details;
	}

	/// <summary>
	/// Builds book details from an already-parsed catalog entry, without a network round-trip.
	/// Used when a server exposes no detail document for the book (e.g. Calibre content servers).
	/// </summary>
	public static BookDetails BuildDetailsFromEntry(OpdsEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		return ToBookDetails(entry, sourceUrl: null);
	}

	static Link ParseLinkElement(XElement linkEl, string? sourceUrl)
	{
		return new Link
		{
			Href = ResolveUri(linkEl, sourceUrl),
			Rel = linkEl.Attribute(Attr.Rel)?.Value ?? Link.RelSelf,
			Type = linkEl.Attribute(Attr.Type)?.Value,
			Length = linkEl.Attribute(Attr.Length)?.Value,
			Title = linkEl.Attribute(Attr.Title)?.Value,
			Language = linkEl.Attribute(Attr.HrefLang)?.Value
		};
	}

	static bool IsImageLink(string? rel, string? type)
	{
		return string.Equals(rel, opdsImageRel, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(rel, opdsImageThumbRel, StringComparison.OrdinalIgnoreCase) ||
			(type is not null && type.StartsWith(Mime.ImagePrefix, StringComparison.OrdinalIgnoreCase));
	}

	static bool LinkIsAcquisition(Link link)
	{
		string? type = link.Type;
		if (string.IsNullOrWhiteSpace(type))
		{
			return link.IsAcquisition();
		}

		string mime = type.Split(';')[0].Trim().ToLowerInvariant();
		return acquisitionTypes.Contains(mime) ||
			mime.Contains("epub", StringComparison.OrdinalIgnoreCase) ||
			mime.Contains("mobi", StringComparison.OrdinalIgnoreCase) ||
			mime.Contains("ebook", StringComparison.OrdinalIgnoreCase);
	}

	static FeedType DetectFeedType(XElement root)
	{
		return FirstLocal(root, Tags.FeedType)?.Value switch
		{
			feedTypeValueNavigation => FeedType.Navigation,
			feedTypeValueAcquisition => FeedType.Acquisition,
			feedTypeValueSearch => FeedType.Search,
			_ => DetectFeedTypeFromLinks(root)
		};
	}

	static FeedType DetectFeedTypeFromLinks(XElement root)
	{
		List<string> types = new();
		foreach (XElement linkEl in Locals(root, Tags.Link))
		{
			if (linkEl.Attribute(Attr.Type)?.Value is { } type)
			{
				types.Add(type);
			}
		}

		return AnyTypeMatches(types, opdsAcquisitionFeedType, opdsAcquisitionFeedEntryType)
			? FeedType.Acquisition
			: AnyTypeMatches(types, opdsSearchFeedType)
			? FeedType.Search
			: AnyTypeMatches(types, opdsNavigationFeedType, opdsNavigationFeedEntryType) ? FeedType.Navigation : FeedType.Unknown;
	}

	static bool AnyTypeMatches(List<string> types, params string[] candidates)
	{
		return candidates.Any(types.Contains);
	}

	static Author? ParseAuthor(XElement? authorEl)
	{
		if (authorEl is null)
		{
			return null;
		}

		string? name = FirstLocal(authorEl, Tags.Name)?.Value;
		return string.IsNullOrWhiteSpace(name)
			? null
			: new Author
			{
				Name = name,
				Uri = FirstLocal(authorEl, Tags.Uri)?.Value
			};
	}

	static XElement? LoadRoot(byte[] content)
	{
		try
		{
			using MemoryStream ms = new(content);
			return XDocument.Load(ms, LoadOptions.None)?.Root;
		}
		catch (System.Xml.XmlException)
		{
			return null;
		}
	}

	static XElement? FirstLocal(XElement parent, string localName)
	{
		return parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
	}

	static IEnumerable<XElement> Locals(XElement parent, string localName)
	{
		return parent.Elements().Where(e => e.Name.LocalName == localName);
	}

	static string NewId()
	{
		return Guid.NewGuid().ToString(guidFormat);
	}

	static string ResolveUri(XElement linkEl, string? baseUri)
	{
		return ResolveUriRaw(linkEl.Attribute(Attr.Href)?.Value ?? string.Empty, baseUri);
	}

	static string ResolveUriRaw(string href, string? baseUri)
	{
		return string.IsNullOrWhiteSpace(href)
			? blankUri
			: Uri.TryCreate(baseUri, UriKind.Absolute, out Uri? baseUriObj)
			? new Uri(baseUriObj, href).ToString()
			: new Uri(href, UriKind.RelativeOrAbsolute).ToString();
	}

	static DateTime? ParseDate(string? value)
	{
		return string.IsNullOrWhiteSpace(value)
			? null
			: DateTime.TryParse(value, CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out global::System.DateTime date)
			? date
			: null;
	}

	static long ParseLength(string? value)
	{
		return long.TryParse(value, out long len) ? len : 0;
	}

	static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
	{
		using MemoryStream ms = new();
		await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
		return ms.ToArray();
	}

	public static string FormatNameFor(string? mimeType)
	{
		if (string.IsNullOrWhiteSpace(mimeType))
		{
			return "File";
		}

		string mime = mimeType.Split(';')[0].Trim().ToLowerInvariant();
		return mime switch
		{
			Mime.Epub or Mime.Epub3 => "EPUB",
			Mime.Pdf => "PDF",
			Mime.Mobi or Mime.Mobipocket or Mime.Mobi10 => "MOBI",
			Mime.Azw3 => "AZW3",
			Mime.Rtf => "RTF",
			Mime.PlainText => "TXT",
			Mime.Html => "HTML",
			Mime.Fb2 => "FB2",
			Mime.Lit => "LIT",
			_ => GetExtensionName(mime) ?? mimeType
		};
	}

	static string? GetExtensionName(string mimeType)
	{
		string extension = Path.GetExtension(mimeType).TrimStart('.');
		return extension.Length > 0 ? extension.ToUpperInvariant() : null;
	}

	static class Tags
	{
		public const string Feed = "feed";
		public const string Entry = "entry";
		public const string Rdf = "RDF";
		public const string Id = "id";
		public const string Title = "title";
		public const string Subtitle = "subtitle";
		public const string Updated = "updated";
		public const string Published = "published";
		public const string Author = "author";
		public const string Name = "name";
		public const string Uri = "uri";
		public const string Link = "link";
		public const string Category = "category";
		public const string Summary = "summary";
		public const string Content = "content";
		public const string Series = "series";
		public const string Language = "language";
		public const string InLanguage = "inLanguage";
		public const string Publisher = "publisher";
		public const string Provider = "provider";
		public const string Identifier = "identifier";
		public const string Description = "description";
		public const string Book = "Book";
		public const string Collection = "Collection";
		public const string Creator = "creator";
		public const string Image = "image";
		public const string Url = "url";
		public const string DatePublished = "datePublished";
		public const string DateCreated = "dateCreated";
		public const string FeedType = "feedType";
		public const string TotalResults = "totalResults";
		public const string TotalPages = "totalPages";
		public const string Page = "page";
	}

	static class Attr
	{
		public const string Href = "href";
		public const string Rel = "rel";
		public const string Type = "type";
		public const string Length = "length";
		public const string Title = "title";
		public const string HrefLang = "hreflang";
		public const string Term = "term";
		public const string Label = "label";
		public const string Scheme = "scheme";
		public const string Authority = "authority";
		public const string Kind = "kind";
		public const string ContentType = "contentType";
		public const string Id = "id";
	}

	static class Mime
	{
		public const string Epub = "application/epub+zip";
		public const string Epub3 = "application/epub3";
		public const string Pdf = "application/pdf";
		public const string Mobi = "application/mobi";
		public const string Mobipocket = "application/x-mobipocket-ebook";
		public const string Mobi10 = "application/x-mobi10-ebook";
		public const string Azw3 = "application/kindle+azw3";
		public const string Audiobook = "application/audiobook+zip";
		public const string Rtf = "application/rtf";
		public const string PlainText = "text/plain";
		public const string Html = "text/html";
		public const string Palm = "application/vnd.palm";
		public const string Fb2 = "application/fb2+zip";
		public const string Lit = "application/lit";
		public const string ImagePrefix = "image/";
	}
}

public sealed class OpdsFeedException(string message) : Exception(message);
