using DisplayBook.Viewer.Models;
using DisplayBook.Viewer.Services;
using Xunit;

namespace DisplayBook.Tests;

public class EpubPublicationParserTests
{
	const string containerXml = """
		<?xml version="1.0"?>
		<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
		  <rootfiles>
		    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
		  </rootfiles>
		</container>
		""";

	[Fact]
	public async Task Parse_SpineOrder_FollowsItemrefSequenceNotManifestOrder()
	{
		Dictionary<string, string> entries = new()
		{
			["META-INF/container.xml"] = containerXml,
			["OEBPS/content.opf"] = """
				<?xml version="1.0"?>
				<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="BookId">
				  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
				    <dc:title>Sample</dc:title>
				    <dc:creator>Author</dc:creator>
				  </metadata>
				  <manifest>
				    <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
				    <item id="ch2" href="ch2.xhtml" media-type="application/xhtml+xml"/>
				    <item id="ch3" href="ch3.xhtml" media-type="application/xhtml+xml"/>
				  </manifest>
				  <spine>
				    <itemref idref="ch2"/>
				    <itemref idref="ch1"/>
				    <itemref idref="ch3"/>
				  </spine>
				</package>
				""",
			["OEBPS/ch1.xhtml"] = "<html><body><p>One</p></body></html>",
			["OEBPS/ch2.xhtml"] = "<html><body><p>Two</p></body></html>",
			["OEBPS/ch3.xhtml"] = "<html><body><p>Three</p></body></html>",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);

		Assert.Equal(["OEBPS/ch2.xhtml", "OEBPS/ch1.xhtml", "OEBPS/ch3.xhtml"], publication.Spine.Select(item => item.Href));
		Assert.Equal([0, 1, 2], publication.Spine.Select(item => item.Index));
		Assert.Equal("Sample", publication.Title);
		Assert.Equal("Author", publication.Author);
	}

	[Fact]
	public async Task Parse_ItemrefLinearNo_ExcludedFromSpine()
	{
		Dictionary<string, string> entries = new()
		{
			["META-INF/container.xml"] = containerXml,
			["OEBPS/content.opf"] = """
				<?xml version="1.0"?>
				<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="BookId">
				  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Sample</dc:title></metadata>
				  <manifest>
				    <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
				    <item id="notes" href="notes.xhtml" media-type="application/xhtml+xml"/>
				  </manifest>
				  <spine>
				    <itemref idref="ch1"/>
				    <itemref idref="notes" linear="no"/>
				  </spine>
				</package>
				""",
			["OEBPS/ch1.xhtml"] = "<html><body><p>One</p></body></html>",
			["OEBPS/notes.xhtml"] = "<html><body><p>Notes</p></body></html>",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);

		Assert.Single(publication.Spine);
		Assert.Equal("OEBPS/ch1.xhtml", publication.Spine[0].Href);
	}

	[Fact]
	public async Task Parse_XhtmlNav_FlattensLinksIntoToc()
	{
		Dictionary<string, string> entries = new()
		{
			["META-INF/container.xml"] = containerXml,
			["OEBPS/content.opf"] = """
				<?xml version="1.0"?>
				<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="BookId">
				  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Sample</dc:title></metadata>
				  <manifest>
				    <item id="ch1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
				    <item id="ch2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
				    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
				  </manifest>
				  <spine>
				    <itemref idref="ch1"/>
				    <itemref idref="ch2"/>
				  </spine>
				</package>
				""",
			["OEBPS/text/ch1.xhtml"] = "<html><body><h1 id=\"top\">One</h1></body></html>",
			["OEBPS/text/ch2.xhtml"] = "<html><body><p>Two</p></body></html>",
			["OEBPS/nav.xhtml"] = """
				<html xmlns:epub="http://www.idpf.org/2007/ops">
				<body>
				  <nav epub:type="toc">
				    <ol>
				      <li><a href="text/ch1.xhtml#top">Chapter One</a></li>
				      <li><a href="text/ch2.xhtml">Chapter Two</a></li>
				    </ol>
				  </nav>
				</body>
				</html>
				""",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);

		Assert.Equal(2, publication.Toc.Count);
		Assert.Equal("Chapter One", publication.Toc[0].Label);
		Assert.Equal(0, publication.Toc[0].SpineIndex);
		Assert.Equal("#top", publication.Toc[0].Fragment);
		Assert.Equal("Chapter Two", publication.Toc[1].Label);
		Assert.Equal(1, publication.Toc[1].SpineIndex);
		Assert.Equal(string.Empty, publication.Toc[1].Fragment);
	}

	[Fact]
	public async Task Parse_NcxOnlyToc_ParsesNavPoints()
	{
		Dictionary<string, string> entries = new()
		{
			["META-INF/container.xml"] = containerXml,
			["OEBPS/content.opf"] = """
				<?xml version="1.0"?>
				<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="BookId">
				  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Sample</dc:title></metadata>
				  <manifest>
				    <item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
				    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
				  </manifest>
				  <spine toc="ncx">
				    <itemref idref="ch1"/>
				  </spine>
				</package>
				""",
			["OEBPS/ch1.xhtml"] = "<html><body><p>One</p></body></html>",
			["OEBPS/toc.ncx"] = """
				<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/">
				  <navMap>
				    <navPoint id="np1">
				      <navLabel><text>Chapter One</text></navLabel>
				      <content src="ch1.xhtml"/>
				    </navPoint>
				  </navMap>
				</ncx>
				""",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);

		EpubTocEntry entry = Assert.Single(publication.Toc);
		Assert.Equal("Chapter One", entry.Label);
		Assert.Equal(0, entry.SpineIndex);
	}
}
