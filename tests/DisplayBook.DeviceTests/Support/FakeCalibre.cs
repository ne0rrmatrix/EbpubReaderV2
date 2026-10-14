using System.Net;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// A small Calibre-like OPDS server on <see cref="FakeNetwork.Opds"/>: a navigation root, a
/// two-page "newest" acquisition feed (one book offered only as MOBI), search, a standalone entry
/// document, and EPUB downloads that are real test books.
/// </summary>
static class FakeCalibre
{
	public const string Root = "http://calibre.test:8080/opds";
	public const string Newest = "http://calibre.test:8080/opds/newest";
	public const string NewestPage2 = "http://calibre.test:8080/opds/newest/2";
	public const string Search = "http://calibre.test:8080/opds/search";
	public const string BrokenFeed = "http://calibre.test:8080/opds/broken";
	public const string EntryDocument = "http://calibre.test:8080/opds/entry/1";

	public static string Download(int bookId) => $"http://calibre.test:8080/get/epub/{bookId}/Calibre_Library";

	/// <summary>Registers every route. The EPUB for book N is a test book titled with its OPDS title.</summary>
	public static void Serve()
	{
		FakeServer server = FakeNetwork.Opds;
		server.OnUrl(Root, $"""
			<feed xmlns="http://www.w3.org/2005/Atom">
			  <id>calibre:root</id>
			  <title>calibre Library</title>
			  <subtitle>Books on the test server</subtitle>
			  <link rel="self" href="/opds" type="application/atom+xml;profile=opds-catalog;kind=navigation"/>
			  <link rel="search" href="/opds/search" type="application/atom+xml"/>
			  <entry>
			    <id>calibre:newest</id>
			    <title>By Newest</title>
			    <summary>Books sorted by date</summary>
			    <link rel="subsection" href="/opds/newest" type="application/atom+xml;profile=opds-catalog;kind=acquisition"/>
			  </entry>
			  <entry>
			    <id>calibre:broken</id>
			    <title>Broken section</title>
			    <link rel="subsection" href="/opds/broken" type="application/atom+xml;profile=opds-catalog;kind=acquisition"/>
			  </entry>
			</feed>
			""");
		server.OnUrl(Newest, Acquisition("By Newest", "/opds/newest/2",
			Book(1, "Elric of Melniboné", "Michael Moorcock", epub: true),
			Book(2, "The Sailor on the Seas of Fate", "Michael Moorcock", epub: true),
			Book(3, "Only In Mobi", "Some Author", epub: false)));
		server.OnUrl(NewestPage2, Acquisition("By Newest", next: null,
			Book(4, "The Weird of the White Wolf", "Michael Moorcock", epub: true)));
		server.On(
			request => request.RequestUri!.AbsoluteUri.StartsWith(Search, StringComparison.Ordinal),
			_ => FakeServer.Text(HttpStatusCode.OK, Acquisition("Search results", next: null,
				Book(5, "Dune", "Frank Herbert", epub: true)), "application/atom+xml"));
		server.OnUrl(BrokenFeed, _ => FakeServer.Text(HttpStatusCode.InternalServerError, "boom", "text/plain"));
		server.OnUrl(EntryDocument, $"""
			<entry xmlns="http://www.w3.org/2005/Atom">
			  <id>calibre:book:1</id>
			  <title>Elric of Melniboné</title>
			  <author><name>Michael Moorcock</name></author>
			  <link rel="http://opds-spec.org/acquisition" type="application/epub+zip" href="{Download(1)}"/>
			</entry>
			""");

		foreach ((int id, string title) in new[] { (1, "Elric of Melniboné"), (2, "The Sailor on the Seas of Fate"), (4, "The Weird of the White Wolf"), (5, "Dune") })
		{
			byte[] epub = TestEpub.Build(title, author: id == 5 ? "Frank Herbert" : "Michael Moorcock");
			server.OnUrl(Download(id), _ => FakeServer.Bytes(epub));
		}
	}

	static string Acquisition(string title, string? next, params string[] entries) => $"""
		<feed xmlns="http://www.w3.org/2005/Atom" xmlns:dc="http://purl.org/dc/terms/">
		  <id>calibre:{Guid.NewGuid():N}</id>
		  <title>{title}</title>
		  <link rel="search" href="/opds/search" type="application/atom+xml"/>
		  {(next is null ? string.Empty : $"""<link rel="next" href="{next}" type="application/atom+xml;profile=opds-catalog;kind=acquisition"/>""")}
		  {string.Concat(entries)}
		</feed>
		""";

	static string Book(int id, string title, string author, bool epub) => $"""
		<entry>
		  <id>calibre:book:{id}</id>
		  <title>{title}</title>
		  <author><name>{author}</name></author>
		  <dc:publisher>Test Press</dc:publisher>
		  <dc:language>en</dc:language>
		  <summary>The story of {title}.</summary>
		  <link rel="http://opds-spec.org/thumbnail" type="image/jpeg" href="/get/thumb/{id}/Calibre_Library?sz=600x800"/>
		  {(epub ? $"""<link rel="http://opds-spec.org/acquisition" type="application/epub+zip" href="/get/epub/{id}/Calibre_Library" length="4096"/>""" : string.Empty)}
		  <link rel="http://opds-spec.org/acquisition" type="application/x-mobipocket-ebook" href="/get/mobi/{id}/Calibre_Library"/>
		</entry>
		""";
}
