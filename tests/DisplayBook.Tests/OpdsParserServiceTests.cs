using System.Linq;
using System.Text;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Xunit;

namespace DisplayBook.Tests;

/// <summary>
/// Tests for <see cref="OpdsParserService"/>.
///
/// HTTP-level methods are exercised through <see cref="OpdsParserService.ParseFeedAsync(Stream,
/// CancellationToken)"/> and the pure byte-level helpers where available, so the tests are fully
/// hermetic (no network, no HTTP server).
/// </summary>
public class OpdsParserServiceTests
{
    private static readonly byte[] AtomCatalog = Xml(
        """
        <feed xmlns="http://www.w3.org/2005/Atom"
              xmlns:opds="http://opds-spec.org/2010/catalog"
              xmlns:dct="http://purl.org/dc/terms/"
              xmlns:dc="http://purl.org/dc/terms/">
          <id>urn:uuid:feed-1</id>
          <title>Test catalog</title>
          <subtitle>Entries used by the OPDS unit tests</subtitle>
          <updated>2024-01-02T03:04:05Z</updated>
          <feedType>navigation</feedType>
          <totalResults>42</totalResults>
          <link rel="self" type="application/atom+xml" href="http://calibre.local:8080/"/>
          <link rel="next" type="application/atom+xml" href="http://calibre.local:8080/?page=1"/>
          <author><name>Calibre</name></author>

          <entry>
            <id>urn:uuid:entry-1</id>
            <title>Sample book</title>
            <summary>First entry.</summary>
            <content type="html">Full text content</content>
            <updated>2024-01-02T03:04:05Z</updated>
            <published>2023-06-15T12:00:00Z</published>
            <author><name>Jane Doe</name></author>
            <series>Test series</series>
            <language>en</language>
            <publisher>Acme Press</publisher>
            <identifier kind="isbn13">978-0-123456-78-9</identifier>
            <category term="Fiction" label="Fiction" scheme="bks:genre"/>
            <link rel="self" type="application/atom+xml" href="http://calibre.local:8080/entry/1"/>
            <link rel="http://opds-spec.org/image" type="image/jpeg"
                  href="http://calibre.local:8080/cover1.jpg" length="1234"/>
            <link rel="http://opds-spec.org/image-thumbnail" type="image/jpeg"
                  href="http://calibre.local:8080/cover1-thumb.jpg"/>
            <link type="application/epub+zip" href="http://calibre.local:8080/download/1"
                  title="EPUB" length="9999"/>
          </entry>

          <entry>
            <id>urn:uuid:entry-2</id>
            <title>Second book</title>
            <link rel="http://opds-spec.org/acquisition" type="application/epub+zip"
                  href="http://calibre.local:8080/download/2"/>
          </entry>
        </feed>
        """
    );

    private static readonly byte[] SingleEntry = Xml(
        """
        <entry xmlns="http://www.w3.org/2005/Atom">
          <id>urn:uuid:solo-1</id>
          <title>Solo book</title>
          <summary>Standalone entry document.</summary>
          <author><name>Bob Smith</name></author>
          <link rel="self" type="application/atom+xml" href="http://calibre.local:8080/entry/solo"/>
          <link type="application/pdf" href="http://calibre.local:8080/download/solo" length="5"/>
        </entry>
        """
    );

    private static readonly byte[] Opds20Rdf = Xml(
        """
        <RDF xmlns="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
             xmlns:opds="http://opds-spec.org/2017/catalog#">
          <Description id="urn:uuid:book-1">
            <name>OPDS 2.0 book</name>
            <summary>RDF description.</summary>
            <creator><Name>Ada Lovelace</Name></creator>
            <datePublished>2022-05-06T00:00:00Z</datePublished>
            <inLanguage>en</inLanguage>
            <provider>Acme</provider>
            <image contentType="image/jpeg">http://calibre.local:8080/cover-rdf.jpg</image>
            <url contentType="application/epub+zip">http://calibre.local:8080/rdf-epub</url>
          </Description>
        </RDF>
        """
    );

    private static readonly byte[] CalibreCatalog = Xml(
        """
        <feed xmlns="http://www.w3.org/2005/Atom"
              xmlns:dc="http://purl.org/dc/terms/">
          <title>calibre Library :: By Newest</title>
          <author><name>calibre</name></author>
          <id>calibre-all:timestamp</id>
          <updated>2026-09-07T19:28:05+00:00</updated>
          <link rel="search" title="Search"
                href="/opds/search/{searchTerms}?library_id=Calibre_Library"
                type="application/atom+xml"/>
          <link href="/opds?library_id=Calibre_Library"
                type="application/atom+xml;type=feed;profile=opds-catalog;kind=navigation"
                rel="up" title="Up"/>
          <entry>
            <title>Elric: The Stealer of Souls</title>
            <author><name>Michael Moorcock</name></author>
            <id>urn:uuid:ed29e908-1643-4bad-a12d-2eaee466accc</id>
            <updated>2026-09-07T19:21:04+00:00</updated>
            <published>2026-09-07T19:14:46.243117+00:00</published>
            <dc:date>2008-02-19T00:00:00+00:00</dc:date>
            <link type="application/epub+zip" href="/get/epub/3820/Calibre_Library"
                  rel="http://opds-spec.org/acquisition" length="2295017"/>
            <link type="application/x-mobi10-ebook" href="/get/kfx/3820/Calibre_Library"
                  rel="http://opds-spec.org/acquisition" length="3124099"/>
            <link type="image/jpeg" href="/get/cover/3820/Calibre_Library"
                  rel="http://opds-spec.org/cover"/>
            <link type="image/jpeg" href="/get/thumb/3820/Calibre_Library"
                  rel="http://opds-spec.org/thumbnail"/>
          </entry>
        </feed>
        """
    );

    private static readonly byte[] Garbage = Encoding.UTF8.GetBytes("not xml at all <<<");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseFeedFromBytes_NullOrWhitespaceSource_IsTolerated(string? ignored)
    {
        // Guards the entry contract: valid XML still parses, sourceUrl may be null.
        var feed = OpdsParserService.ParseFeedFromBytes(Garbage, ignored);
        Assert.Null(feed);
    }

    [Fact]
    public void ParseFeedFromBytes_GarbageData_ReturnsNull()
    {
        Assert.Null(OpdsParserService.ParseFeedFromBytes(Garbage, "http://x/y"));
    }

    [Fact]
    public void ParseFeedFromBytes_AtomCatalog_ParsesFeedMetadata()
    {
        var feed = OpdsParserService.ParseFeedFromBytes(AtomCatalog, "http://calibre.local:8080/");
        Assert.NotNull(feed);
        Assert.Equal("Test catalog", feed!.Title);
        Assert.Equal("Entries used by the OPDS unit tests", feed.Subtitle);
        Assert.Equal("urn:uuid:feed-1", feed.Id);
        Assert.Equal(FeedType.Navigation, feed.FeedType);
        Assert.NotNull(feed.Updated);
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), feed.Updated!.Value);
        Assert.Equal("42", feed.ExtendedFeedMetadata["totalResults"]);
        Assert.Equal("Calibre", feed.Author?.Name);
    }

    [Fact]
    public void ParseFeedFromBytes_AtomCatalog_ParsesEntriesAndCounts()
    {
        var feed = OpdsParserService.ParseFeedFromBytes(AtomCatalog, "http://calibre.local:8080/")!;
        Assert.Equal(2, feed.Entries.Count);
        Assert.Equal("Sample book", feed.Entries[0].Title);
        Assert.Equal("Second book", feed.Entries[1].Title);
    }

    [Fact]
    public void ParseFeedFromBytes_AtomCatalog_ParsesEntryMetadata()
    {
        var entry = OpdsParserService.ParseFeedFromBytes(AtomCatalog, "http://calibre.local:8080/")!.Entries[0];
        Assert.Equal("First entry.", entry.Summary);
        Assert.Equal("Full text content", entry.Content);
        Assert.Equal("Jane Doe", entry.Authors[0].Name);
        Assert.Equal("Test series", entry.Series);
        // The series is mirrored into Categories as its first entry; the feed-level
        // <category> (Fiction) follows it.
        Assert.Equal("Test series", entry.Categories[0].Term);
        Assert.Equal("Fiction", entry.Categories[1].Term);
        Assert.Equal("en", entry.ExtendedMetadata["language"]);
        Assert.Equal("Acme Press", entry.ExtendedMetadata["publisher"]);
        Assert.Equal("978-0-123456-78-9", entry.Identifiers["isbn13"]);
        Assert.NotNull(entry.Published);
        Assert.Equal(new DateTime(2023, 6, 15, 12, 0, 0, DateTimeKind.Utc), entry.Published!.Value);
    }

    [Fact]
    public void ParseFeedFromBytes_AtomCatalog_CoverPrefersFullImageAndKeepsThumbnail()
    {
        var entry = OpdsParserService.ParseFeedFromBytes(AtomCatalog, "http://calibre.local:8080/")!.Entries[0];
        Assert.NotNull(entry.Cover);
        Assert.Equal("http://calibre.local:8080/cover1.jpg", entry.Cover!.Url);
        Assert.Equal("image/jpeg", entry.Cover.Format);
        Assert.Equal(1234, entry.Cover.Size);
        Assert.Equal("http://calibre.local:8080/cover1-thumb.jpg", entry.Cover.ThumbnailUrl);
    }

    [Fact]
    public void ParseFeedFromBytes_AtomCatalog_ImageLinksAreNotAcquisitionLinks()
    {
        var entry = OpdsParserService.ParseFeedFromBytes(AtomCatalog, "http://calibre.local:8080/")!.Entries[0];
        // Cover/thumbnail links must not leak into the entry link list.
        Assert.DoesNotContain(entry.Links, link => link.Href.EndsWith(".jpg"));
        // The entry keeps its self link plus exactly one EPUB acquisition link.
        Assert.Single(entry.Links, link => link.Type == "application/epub+zip");
    }

    [Fact]
    public void ParseFeedFromBytes_AtomCatalog_PaginationIsDerivedFromLinks()
    {
        var feed = OpdsParserService.ParseFeedFromBytes(AtomCatalog, "http://calibre.local:8080/")!;
        Assert.NotNull(feed.Pagination);
        Assert.True(feed.Pagination!.HasNext);
        Assert.False(feed.Pagination.HasPrevious);
        Assert.Equal("http://calibre.local:8080/?page=1", feed.Pagination.NextUrl);
    }

    [Fact]
    public void ParseFeedFromBytes_SingleEntryDocument_WrapsInFeed()
    {
        var feed = OpdsParserService.ParseFeedFromBytes(SingleEntry, "http://calibre.local:8080/entry/solo");
        Assert.NotNull(feed);
        Assert.Single(feed!.Entries);
        Assert.Equal("Solo book", feed.Entries[0].Title);
        Assert.Equal("Bob Smith", feed.Entries[0].Authors[0].Name);
    }

    [Fact]
    public void ParseFeedFromBytes_Opds20Rdf_ParsesBookDescription()
    {
        var feed = OpdsParserService.ParseFeedFromBytes(Opds20Rdf, "http://calibre.local:8080/rdf");
        Assert.NotNull(feed);
        Assert.Single(feed!.Entries);

        var entry = feed.Entries[0];
        Assert.Equal("OPDS 2.0 book", entry.Title);
        Assert.Equal("RDF description.", entry.Summary);
        Assert.Equal("Ada Lovelace", entry.Authors[0].Name);
        // The parser maps inLanguage -> "language" and provider -> "publisher".
        Assert.Equal("en", entry.ExtendedMetadata["language"]);
        Assert.Equal("Acme", entry.ExtendedMetadata["publisher"]);
        Assert.Equal("http://calibre.local:8080/cover-rdf.jpg", entry.Cover?.Url);
        Assert.Contains(entry.Links, link =>
            link.Type == "application/epub+zip" && link.Href == "http://calibre.local:8080/rdf-epub");
    }

    [Fact]
    public async Task ParseFeedAsync_Stream_ParsesAtomCatalog()
    {
        var parser = new OpdsParserService(new HttpClient());
        using var stream = new MemoryStream(AtomCatalog);
        var feed = await parser.ParseFeedAsync(stream);
        Assert.Equal("Test catalog", feed.Title);
        Assert.Equal(2, feed.Entries.Count);
    }

    [Fact]
    public async Task ParseFeedAsync_Stream_Garbage_ThrowsOpdsFeedException()
    {
        var parser = new OpdsParserService(new HttpClient());
        using var stream = new MemoryStream(Garbage);
        await Assert.ThrowsAsync<OpdsFeedException>(() => parser.ParseFeedAsync(stream));
    }

    [Fact]
    public void ParseFeedFromBytes_CalibreCatalog_RecognizesAcquisitionRelLinks()
    {
        var feed = OpdsParserService.ParseFeedFromBytes(CalibreCatalog, "http://calibre.local:8012/opds/navcatalog/newest");
        Assert.NotNull(feed);

        var entry = feed!.Entries[0];
        var epub = entry.Links.First(link => link.Type == "application/epub+zip");
        // Calibre marks download links with the OPDS acquisition rel, not rel="self".
        Assert.True(epub.IsAcquisition());
        Assert.False(epub.IsNavigation());
        Assert.Equal("http://calibre.local:8012/get/epub/3820/Calibre_Library", epub.Href);
        // The two download links (epub, kfx) stay in entry.Links so details can offer them.
        Assert.Equal(2, entry.Links.Count);
        // Cover/thumbnail image links are routed to entry.Cover, not the link list.
        Assert.NotNull(entry.Cover);
        Assert.Equal("http://calibre.local:8012/get/cover/3820/Calibre_Library", entry.Cover!.Url);
        Assert.DoesNotContain(entry.Links, link => link.Type is not null && link.Type.StartsWith("image/"));
    }

    [Fact]
    public void BuildDetailsFromEntry_CalibreEntry_KeepsMetadataAndDownloads()
    {
        var feed = OpdsParserService.ParseFeedFromBytes(CalibreCatalog, "http://calibre.local:8012/opds/navcatalog/newest");
        var entry = feed!.Entries[0];

        var details = OpdsParserService.BuildDetailsFromEntry(entry);
        Assert.Equal("Elric: The Stealer of Souls", details.Title);
        Assert.Equal("Michael Moorcock", details.Authors[0].Name);
        Assert.NotNull(details.Cover);
        Assert.Equal(2, details.DownloadLinks.Count);
        Assert.Contains(details.DownloadLinks, link => link.FormatName == "EPUB" && link.Size == 2295017);
        Assert.Contains(details.DownloadLinks, link => link.FormatName == "MOBI" && link.Size == 3124099);
    }

    [Fact]
    public void FormatNameFor_KnownMimesMapsToHumanNames()
    {
        Assert.Equal("EPUB", OpdsParserService.FormatNameFor("application/epub+zip"));
        Assert.Equal("EPUB", OpdsParserService.FormatNameFor("application/epub3"));
        Assert.Equal("PDF", OpdsParserService.FormatNameFor("application/pdf"));
        Assert.Equal("MOBI", OpdsParserService.FormatNameFor("application/mobi"));
        Assert.Equal("MOBI", OpdsParserService.FormatNameFor("application/x-mobipocket-ebook"));
        Assert.Equal("MOBI", OpdsParserService.FormatNameFor("application/x-mobi10-ebook"));
        Assert.Equal("AZW3", OpdsParserService.FormatNameFor("application/kindle+azw3"));
        Assert.Equal("RTF", OpdsParserService.FormatNameFor("application/rtf"));
        Assert.Equal("TXT", OpdsParserService.FormatNameFor("text/plain"));
        Assert.Equal("FB2", OpdsParserService.FormatNameFor("application/fb2+zip"));
        Assert.Equal("LIT", OpdsParserService.FormatNameFor("application/lit"));
    }

    [Fact]
    public void FormatNameFor_ParameterizedMime_IgnoresCharset()
    {
        Assert.Equal("EPUB", OpdsParserService.FormatNameFor("application/epub+zip; charset=utf-8"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatNameFor_NullOrWhitespace_ReturnsFile(string? mimeType)
    {
        Assert.Equal("File", OpdsParserService.FormatNameFor(mimeType));
    }

    [Fact]
    public void FormatNameFor_UnknownMime_FallsBackToExtensionOrOriginal()
    {
        Assert.Equal("HTML", OpdsParserService.FormatNameFor("text/html"));
        Assert.Equal("audio/mpeg", OpdsParserService.FormatNameFor("audio/mpeg"));
    }

    [Theory]
    [InlineData("http://opds-spec.org/acquisition", true, false)]
    [InlineData("http://opds-spec.org/sale", true, false)]
    [InlineData("http://opds-spec.org/sample", true, false)]
    [InlineData("self", true, false)]
    [InlineData("next", false, true)]
    [InlineData(null, true, false)]
    [InlineData("", true, false)]
    public void Link_RelClassification_FollowsOpdsRelations(string? rel, bool expectedAcquisition, bool expectedNavigation)
    {
        var link = new Link { Href = "http://calibre.local:8012/x", Rel = rel };
        Assert.Equal(expectedAcquisition, link.IsAcquisition());
        Assert.Equal(expectedNavigation, link.IsNavigation());
    }

    private static byte[] Xml(string document) => Encoding.UTF8.GetBytes(document);
}
