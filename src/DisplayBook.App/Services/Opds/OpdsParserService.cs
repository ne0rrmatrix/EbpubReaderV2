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
    private const string OpdsImageRel = "http://opds-spec.org/image";
    private const string OpdsImageThumbRel = "http://opds-spec.org/image-thumbnail";
    private const string OpdsThumbnailRel = "http://opds-spec.org/thumbnail";
    private const string OpdsAcquisitionFeedType = "http://opds-spec.org/acquisition";
    private const string OpdsAcquisitionFeedEntryType = "http://opds-spec.org/acquisition-feed";
    private const string OpdsNavigationFeedType = "http://opds-spec.org/navigation";
    private const string OpdsNavigationFeedEntryType = "http://opds-spec.org/navigation-feed";
    private const string OpdsSearchFeedType = "http://opds-spec.org/search-feed";

    private const string FeedTypeValueNavigation = "navigation";
    private const string FeedTypeValueAcquisition = "acquisition";
    private const string FeedTypeValueSearch = "search";

    private const string DefaultTitle = "Untitled";
    private const string DefaultCatalogTitle = "OPDS catalog";
    private const string BlankUri = "about:blank";
    private const string SeriesScheme = "series";
    private const string ContentTypeMetadataKey = "contentType";
    private const string IdKindDefault = "identifier";
    private const string GuidFormat = "N";

    private static readonly string[] AcquisitionTypes =
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

        using var response = await httpClient.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var content = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);

        var feed = await Task.Run(() => ParseFeedFromBytes(content, feedUrl), cancellationToken).ConfigureAwait(false);
        return feed ?? throw new OpdsFeedException($"The document at '{feedUrl}' is not a valid OPDS feed.");
    }

    public Task<OpdsFeed> ParseFeedAsync(Stream feedStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedStream);
        return Task.Run(() =>
        {
            using var reader = new StreamReader(feedStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var text = reader.ReadToEnd();
            return ParseFeedFromBytes(Encoding.UTF8.GetBytes(text), sourceUrl: null)
                ?? throw new OpdsFeedException("The provided stream is not a valid OPDS feed.");
        }, cancellationToken);
    }

    public async Task<BookDetails> ParseBookDetailsAsync(string entryUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryUrl);

        using var response = await httpClient.GetAsync(entryUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var content = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);

        var details = await Task.Run(() => ParseBookDetailsFromBytes(content, entryUrl), cancellationToken).ConfigureAwait(false);
        if (details is not null)
        {
            return details;
        }

        // Some servers serve an acquisition feed for an entry URL; fall back to the first entry.
        var feed = await Task.Run(() => ParseFeedFromBytes(content, entryUrl), cancellationToken).ConfigureAwait(false);
        if (feed is not null && feed.Entries.Count > 0)
        {
            return ToBookDetails(feed.Entries[0], entryUrl);
        }

        throw new OpdsFeedException($"The document at '{entryUrl}' is not a valid OPDS book document.");
    }

    public Task<BookDetails> ParseBookDetailsAsync(OpdsEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Task.Run(() => BuildDetailsFromEntry(entry), cancellationToken);
    }

    public async Task<IEnumerable<DownloadLink>> GetDownloadLinksAsync(string acquisitionUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acquisitionUrl);

        using var response = await httpClient.GetAsync(acquisitionUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var content = await ReadAllBytesAsync(stream, cancellationToken).ConfigureAwait(false);

        return await Task.Run(() => GetDownloadLinksFromBytes(content, acquisitionUrl), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pure, synchronous parse from bytes. Returns <see langword="null"/> when the document is not a recognized feed.
    /// </summary>
    public static OpdsFeed? ParseFeedFromBytes(byte[] content, string? sourceUrl)
    {
        var root = LoadRoot(content);
        if (root is null)
        {
            return null;
        }

        return root.Name.LocalName switch
        {
            Tags.Feed => ParseAtomFeed(root, sourceUrl),
            Tags.Entry => BuildSingleEntryFeed(ParseAtomEntry(root, sourceUrl), sourceUrl),
            Tags.Rdf => ParseOpds20Rdf(root, sourceUrl),
            _ => null
        };
    }

    private static List<DownloadLink> GetDownloadLinksFromBytes(byte[] content, string? sourceUrl)
    {
        var details = ParseBookDetailsFromBytes(content, sourceUrl);
        if (details is not null)
        {
            return details.DownloadLinks;
        }

        var feed = ParseFeedFromBytes(content, sourceUrl);
        if (feed is null)
        {
            return [];
        }

        var links = new List<DownloadLink>();
        foreach (var entry in feed.Entries)
        {
            foreach (var link in entry.Links.Where(LinkIsAcquisition))
            {
                links.Add(ToDownloadLink(link));
            }
        }

        return links;
    }

    private static DownloadLink ToDownloadLink(Link link)
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

    private static OpdsFeed BuildSingleEntryFeed(OpdsEntry entry, string? sourceUrl)
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

    private static OpdsFeed ParseAtomFeed(XElement root, string? sourceUrl)
    {
        var feed = CreateFeed(root, sourceUrl);
        ParseFeedLinks(feed, root, sourceUrl);
        ParseFeedMetadata(feed, root);

        foreach (var entryEl in Locals(root, Tags.Entry))
        {
            feed.Entries.Add(ParseAtomEntry(entryEl, sourceUrl));
        }

        return feed;
    }

    private static OpdsFeed CreateFeed(XElement root, string? sourceUrl)
    {
        return new OpdsFeed
        {
            Id = FirstLocal(root, Tags.Id)?.Value ?? string.Empty,
            Title = FirstLocal(root, Tags.Title)?.Value ?? DefaultCatalogTitle,
            Subtitle = FirstLocal(root, Tags.Subtitle)?.Value ?? string.Empty,
            Updated = ParseDate(FirstLocal(root, Tags.Updated)?.Value),
            Author = ParseAuthor(FirstLocal(root, Tags.Author)),
            FeedType = DetectFeedType(root),
            SourceUrl = sourceUrl
        };
    }

    private static void ParseFeedLinks(OpdsFeed feed, XElement root, string? sourceUrl)
    {
        foreach (var linkEl in Locals(root, Tags.Link))
        {
            feed.Links.Add(ParseLinkElement(linkEl, sourceUrl));
        }

        var next = feed.GetNextLink();
        var prev = feed.GetPrevLink();
        feed.Pagination = new PaginationInfo
        {
            HasNext = next is not null,
            HasPrevious = prev is not null,
            NextUrl = next?.Href,
            PreviousUrl = prev?.Href
        };
    }

    private static void ParseFeedMetadata(OpdsFeed feed, XElement root)
    {
        AddFeedMetadata(feed, root, Tags.TotalResults);
        AddFeedMetadata(feed, root, Tags.TotalPages);
        AddFeedMetadata(feed, root, Tags.Page);
    }

    private static void AddFeedMetadata(OpdsFeed feed, XElement root, string tag)
    {
        if (FirstLocal(root, tag)?.Value is { } value)
        {
            feed.ExtendedFeedMetadata[tag] = value;
        }
    }

    private static OpdsEntry ParseAtomEntry(XElement entryEl, string? sourceUrl)
    {
        var entry = CreateEntry(entryEl);
        ParseEntryTextFields(entry, entryEl);
        ParseEntryPeopleAndTags(entry, entryEl);
        ParseEntryLinks(entry, entryEl, sourceUrl);
        return entry;
    }

    private static OpdsEntry CreateEntry(XElement entryEl)
    {
        return new OpdsEntry
        {
            Id = FirstLocal(entryEl, Tags.Id)?.Value ?? NewId(),
            Title = FirstLocal(entryEl, Tags.Title)?.Value ?? DefaultTitle
        };
    }

    private static void ParseEntryTextFields(OpdsEntry entry, XElement entryEl)
    {
        entry.Updated = ParseDate(FirstLocal(entryEl, Tags.Updated)?.Value);
        entry.Published = ParseDate(FirstLocal(entryEl, Tags.Published)?.Value);
        entry.Summary = FirstLocal(entryEl, Tags.Summary)?.Value;

        var contentEl = FirstLocal(entryEl, Tags.Content);
        entry.Content = contentEl?.Value;
        if (contentEl?.Attribute(Attr.Type)?.Value is { } contentType)
        {
            entry.ExtendedMetadata[ContentTypeMetadataKey] = contentType;
        }

        entry.Series = FirstLocal(entryEl, Tags.Series)?.Value;
        if (!string.IsNullOrWhiteSpace(entry.Series))
        {
            entry.Categories.Add(new OpdsCategory { Term = entry.Series, Label = entry.Series, Scheme = SeriesScheme });
        }

        entry.ExtendedMetadata[Tags.Language] = FirstLocal(entryEl, Tags.Language)?.Value;
        entry.ExtendedMetadata[Tags.Publisher] = FirstLocal(entryEl, Tags.Publisher)?.Value;

        foreach (var idEl in Locals(entryEl, Tags.Identifier))
        {
            entry.Identifiers[idEl.Attribute(Attr.Kind)?.Value ?? IdKindDefault] = idEl.Value;
        }
    }

    private static void ParseEntryPeopleAndTags(OpdsEntry entry, XElement entryEl)
    {
        foreach (var authorEl in Locals(entryEl, Tags.Author))
        {
            if (ParseAuthor(authorEl) is { } author)
            {
                entry.Authors.Add(author);
            }
        }

        foreach (var catEl in Locals(entryEl, Tags.Category))
        {
            if (ParseCategory(catEl) is { } category)
            {
                entry.Categories.Add(category);
            }
        }
    }

    private static OpdsCategory? ParseCategory(XElement catEl)
    {
        var term = catEl.Attribute(Attr.Term)?.Value;
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        return new OpdsCategory
        {
            Term = term,
            Label = catEl.Attribute(Attr.Label)?.Value,
            Scheme = catEl.Attribute(Attr.Scheme)?.Value ?? catEl.Attribute(Attr.Authority)?.Value
        };
    }

    private static void ParseEntryLinks(OpdsEntry entry, XElement entryEl, string? sourceUrl)
    {
        foreach (var linkEl in Locals(entryEl, Tags.Link))
        {
            var rel = linkEl.Attribute(Attr.Rel)?.Value;
            if (!IsImageLink(rel, linkEl.Attribute(Attr.Type)?.Value))
            {
                entry.Links.Add(ParseLinkElement(linkEl, sourceUrl));
                continue;
            }

            var isThumbnail = string.Equals(rel, OpdsImageThumbRel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rel, OpdsThumbnailRel, StringComparison.OrdinalIgnoreCase);
            ApplyCover(entry, CreateCoverImage(linkEl, sourceUrl), isThumbnail);
        }
    }

    private static OpdsCoverImage CreateCoverImage(XElement linkEl, string? sourceUrl)
    {
        return new OpdsCoverImage
        {
            Url = ResolveUri(linkEl, sourceUrl),
            Format = linkEl.Attribute(Attr.Type)?.Value,
            Size = ParseLength(linkEl.Attribute(Attr.Length)?.Value)
        };
    }

    private static void ApplyCover(OpdsEntry entry, OpdsCoverImage image, bool isThumbnail)
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

    private static OpdsFeed? ParseOpds20Rdf(XElement root, string? sourceUrl)
    {
        var descriptions = GetRdfDescriptions(root);
        if (descriptions.Count == 0)
        {
            return null;
        }

        var feed = new OpdsFeed
        {
            Title = FirstLocal(root, Tags.Title)?.Value ?? DefaultCatalogTitle,
            SourceUrl = sourceUrl
        };

        foreach (var desc in descriptions)
        {
            feed.Entries.Add(CreateRdfEntry(desc, sourceUrl));
        }

        feed.FeedType = feed.Entries.All(e => e.Links.Any(LinkIsAcquisition))
            ? FeedType.Acquisition
            : FeedType.Unknown;

        return feed;
    }

    private static List<XElement> GetRdfDescriptions(XElement root)
    {
        // RDF book/collection containers may be written with different casing (e.g. <Description>),
        // so match the description container case-insensitively.
        var descriptions = root.Elements()
            .Where(e => string.Equals(e.Name.LocalName, Tags.Description, StringComparison.OrdinalIgnoreCase))
            .ToList();
        descriptions.AddRange(Locals(root, Tags.Book));
        descriptions.AddRange(Locals(root, Tags.Collection));
        return descriptions;
    }

    private static OpdsEntry CreateRdfEntry(XElement desc, string? sourceUrl)
    {
        var entry = new OpdsEntry
        {
            Id = desc.Attribute(Attr.Id)?.Value ?? FirstLocal(desc, Tags.Identifier)?.Value ?? NewId(),
            Title = FirstLocal(desc, Tags.Name)?.Value ?? FirstLocal(desc, Tags.Title)?.Value ?? DefaultTitle,
            Summary = FirstLocal(desc, Tags.Description)?.Value ?? FirstLocal(desc, Tags.Summary)?.Value,
            Published = ParseDate(FirstLocal(desc, Tags.DatePublished)?.Value ?? FirstLocal(desc, Tags.DateCreated)?.Value)
        };

        foreach (var authorEl in Locals(desc, Tags.Author).Concat(Locals(desc, Tags.Creator)))
        {
            var name = FirstLocal(authorEl, Tags.Name)?.Value ?? authorEl.Value;
            if (!string.IsNullOrWhiteSpace(name))
            {
                entry.Authors.Add(new Author { Name = name });
            }
        }

        foreach (var linkEl in Locals(desc, Tags.Url).Concat(Locals(desc, Tags.Image)))
        {
            var isImage = string.Equals(linkEl.Name.LocalName, Tags.Image, StringComparison.Ordinal);
            var contentType = linkEl.Attribute(Attr.ContentType)?.Value ?? linkEl.Element(Tags.Content)?.Value;
            var href = ResolveUriRaw(linkEl.Value, sourceUrl);
            if (isImage)
            {
                entry.Cover = new OpdsCoverImage { Url = href, Format = contentType };
            }
            else
            {
                entry.Links.Add(new Link { Href = href, Rel = Link.RelSelf, Type = contentType, Title = entry.Title });
            }
        }

        entry.ExtendedMetadata[Tags.Language] = (FirstLocal(desc, Tags.InLanguage) ?? FirstLocal(desc, Tags.Language))?.Value;
        entry.ExtendedMetadata[Tags.Publisher] = (FirstLocal(desc, Tags.Provider) ?? FirstLocal(desc, Tags.Publisher))?.Value;

        return entry;
    }

    private static BookDetails? ParseBookDetailsFromBytes(byte[] content, string? sourceUrl)
    {
        var root = LoadRoot(content);
        if (root is null)
        {
            return null;
        }

        return root.Name.LocalName switch
        {
            Tags.Entry => ToBookDetails(ParseAtomEntry(root, sourceUrl), sourceUrl),
            Tags.Rdf => FirstRdfEntry(root, sourceUrl) is { } rdf ? ToBookDetails(rdf, sourceUrl) : null,
            Tags.Feed => FirstFeedEntry(root, sourceUrl) is { } feed ? ToBookDetails(feed, sourceUrl) : null,
            _ => null
        };
    }

    private static OpdsEntry? FirstRdfEntry(XElement root, string? sourceUrl)
    {
        var feed = ParseOpds20Rdf(root, sourceUrl);
        return feed is null || feed.Entries.Count == 0 ? null : feed.Entries[0];
    }

    private static OpdsEntry? FirstFeedEntry(XElement root, string? sourceUrl)
    {
        var feed = ParseAtomFeed(root, sourceUrl);
        return feed.Entries.Count == 0 ? null : feed.Entries[0];
    }

    private static BookDetails ToBookDetails(OpdsEntry entry, string? sourceUrl)
    {
        var details = new BookDetails
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

        if (entry.ExtendedMetadata.TryGetValue(Tags.Language, out var language))
        {
            details.Language = language?.ToString();
        }

        if (entry.ExtendedMetadata.TryGetValue(Tags.Publisher, out var publisher))
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

    private static Link ParseLinkElement(XElement linkEl, string? sourceUrl)
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

    private static bool IsImageLink(string? rel, string? type)
    {
        return string.Equals(rel, OpdsImageRel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rel, OpdsImageThumbRel, StringComparison.OrdinalIgnoreCase) ||
            (type is not null && type.StartsWith(Mime.ImagePrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LinkIsAcquisition(Link link)
    {
        var type = link.Type;
        if (string.IsNullOrWhiteSpace(type))
        {
            return link.IsAcquisition();
        }

        var mime = type.Split(';')[0].Trim().ToLowerInvariant();
        return AcquisitionTypes.Contains(mime) ||
            mime.Contains("epub", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("mobi", StringComparison.OrdinalIgnoreCase) ||
            mime.Contains("ebook", StringComparison.OrdinalIgnoreCase);
    }

    private static FeedType DetectFeedType(XElement root)
    {
        return FirstLocal(root, Tags.FeedType)?.Value switch
        {
            FeedTypeValueNavigation => FeedType.Navigation,
            FeedTypeValueAcquisition => FeedType.Acquisition,
            FeedTypeValueSearch => FeedType.Search,
            _ => DetectFeedTypeFromLinks(root)
        };
    }

    private static FeedType DetectFeedTypeFromLinks(XElement root)
    {
        var types = new List<string>();
        foreach (var linkEl in Locals(root, Tags.Link))
        {
            if (linkEl.Attribute(Attr.Type)?.Value is { } type)
            {
                types.Add(type);
            }
        }

        if (AnyTypeMatches(types, OpdsAcquisitionFeedType, OpdsAcquisitionFeedEntryType))
        {
            return FeedType.Acquisition;
        }

        if (AnyTypeMatches(types, OpdsSearchFeedType))
        {
            return FeedType.Search;
        }

        if (AnyTypeMatches(types, OpdsNavigationFeedType, OpdsNavigationFeedEntryType))
        {
            return FeedType.Navigation;
        }

        return FeedType.Unknown;
    }

    private static bool AnyTypeMatches(List<string> types, params string[] candidates)
    {
        return candidates.Any(types.Contains);
    }

    private static Author? ParseAuthor(XElement? authorEl)
    {
        if (authorEl is null)
        {
            return null;
        }

        var name = FirstLocal(authorEl, Tags.Name)?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new Author
        {
            Name = name,
            Uri = FirstLocal(authorEl, Tags.Uri)?.Value
        };
    }

    private static XElement? LoadRoot(byte[] content)
    {
        try
        {
            using var ms = new MemoryStream(content);
            return XDocument.Load(ms, LoadOptions.None)?.Root;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static XElement? FirstLocal(XElement parent, string localName)
    {
        return parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
    }

    private static IEnumerable<XElement> Locals(XElement parent, string localName)
    {
        return parent.Elements().Where(e => e.Name.LocalName == localName);
    }

    private static string NewId()
    {
        return Guid.NewGuid().ToString(GuidFormat);
    }

    private static string ResolveUri(XElement linkEl, string? baseUri)
    {
        return ResolveUriRaw(linkEl.Attribute(Attr.Href)?.Value ?? string.Empty, baseUri);
    }

    private static string ResolveUriRaw(string href, string? baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return BlankUri;
        }

        if (Uri.TryCreate(baseUri, UriKind.Absolute, out var baseUriObj))
        {
            return new Uri(baseUriObj, href).ToString();
        }

        return new Uri(href, UriKind.RelativeOrAbsolute).ToString();
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    private static long ParseLength(string? value)
    {
        return long.TryParse(value, out var len) ? len : 0;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    public static string FormatNameFor(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return "File";
        }

        var mime = mimeType.Split(';')[0].Trim().ToLowerInvariant();
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

    private static string? GetExtensionName(string mimeType)
    {
        var extension = Path.GetExtension(mimeType).TrimStart('.');
        return extension.Length > 0 ? extension.ToUpperInvariant() : null;
    }

    private static class Tags
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

    private static class Attr
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

    private static class Mime
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
