using System.Text;
using DisplayBook.Viewer.Models;
using DisplayBook.Viewer.Services;
using Xunit;

namespace DisplayBook.Tests;

public class CombinedDocumentBuilderTests
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
	public async Task Build_MultipleChapters_WrapsEachInHiddenSectionInSpineOrder()
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
				    <item id="ch2" href="ch2.xhtml" media-type="application/xhtml+xml"/>
				  </manifest>
				  <spine><itemref idref="ch1"/><itemref idref="ch2"/></spine>
				</package>
				""",
			["OEBPS/ch1.xhtml"] = "<html><body><p>First chapter text</p></body></html>",
			["OEBPS/ch2.xhtml"] = "<html><body><p>Second chapter text</p></body></html>",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);
		string html = Encoding.UTF8.GetString(CombinedDocumentBuilder.Build(archive, publication));

		Assert.Contains("section[data-chapter-index]{display:none}", html);
		Assert.Contains("data-chapter-index=\"0\"", html);
		Assert.Contains("data-chapter-index=\"1\"", html);
		Assert.Contains("First chapter text", html);
		Assert.Contains("Second chapter text", html);
		Assert.True(html.IndexOf("First chapter text", StringComparison.Ordinal) <
			html.IndexOf("Second chapter text", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Build_RewritesImageSrcRelativeToCombinedDocumentLocation()
	{
		Dictionary<string, string> entries = new()
		{
			["META-INF/container.xml"] = containerXml,
			["OEBPS/content.opf"] = """
				<?xml version="1.0"?>
				<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="BookId">
				  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Sample</dc:title></metadata>
				  <manifest><item id="ch1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/></manifest>
				  <spine><itemref idref="ch1"/></spine>
				</package>
				""",
			// Chapter lives two levels deep (OEBPS/text/), and references an image one level up
			// via "../images/cover.jpg" -- relative to the CHAPTER's own directory, that resolves
			// to "OEBPS/images/cover.jpg". Rewritten relative to the combined document's fixed
			// location (one level under the book root, same as the reader shell), that must become
			// "../OEBPS/images/cover.jpg".
			["OEBPS/text/ch1.xhtml"] = "<html><body><img src=\"../images/cover.jpg\"/></body></html>",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);
		string html = Encoding.UTF8.GetString(CombinedDocumentBuilder.Build(archive, publication));

		Assert.Contains("src=\"../OEBPS/images/cover.jpg\"", html);
	}

	[Fact]
	public async Task Build_InlinesBookCssWithRewrittenUrls()
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
				    <item id="css" href="styles/book.css" media-type="text/css"/>
				  </manifest>
				  <spine><itemref idref="ch1"/></spine>
				</package>
				""",
			["OEBPS/ch1.xhtml"] = "<html><body><p>Text</p></body></html>",
			["OEBPS/styles/book.css"] = "body { background: url(\"../images/bg.png\"); }",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);
		string html = Encoding.UTF8.GetString(CombinedDocumentBuilder.Build(archive, publication));

		Assert.Contains("url(\"../OEBPS/images/bg.png\")", html);
	}

	[Fact]
	public async Task Build_MalformedChapterMarkup_FallsBackToRawBytesInsteadOfFailingWholeBook()
	{
		Dictionary<string, string> entries = new()
		{
			["META-INF/container.xml"] = containerXml,
			["OEBPS/content.opf"] = """
				<?xml version="1.0"?>
				<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="BookId">
				  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Sample</dc:title></metadata>
				  <manifest>
				    <item id="bad" href="bad.xhtml" media-type="application/xhtml+xml"/>
				    <item id="good" href="good.xhtml" media-type="application/xhtml+xml"/>
				  </manifest>
				  <spine><itemref idref="bad"/><itemref idref="good"/></spine>
				</package>
				""",
			// Mismatched tag -- not well-formed XML.
			["OEBPS/bad.xhtml"] = "<html><body><p>Broken<div></p></body></html>",
			["OEBPS/good.xhtml"] = "<html><body><p>Fine chapter</p></body></html>",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);
		byte[] result = CombinedDocumentBuilder.Build(archive, publication);
		string html = Encoding.UTF8.GetString(result);

		Assert.Contains("Broken", html);
		Assert.Contains("Fine chapter", html);
	}

	[Fact]
	public async Task Build_ChapterWithUndeclaredHtmlNamedEntity_StillRewritesAssetUrls()
	{
		Dictionary<string, string> entries = new()
		{
			["META-INF/container.xml"] = containerXml,
			["OEBPS/content.opf"] = """
				<?xml version="1.0"?>
				<package xmlns="http://www.idpf.org/2007/opf" unique-identifier="BookId">
				  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Sample</dc:title></metadata>
				  <manifest><item id="ch1" href="ch1.xhtml" media-type="application/xhtml+xml"/></manifest>
				  <spine><itemref idref="ch1"/></spine>
				</package>
				""",
			// &nbsp; has no meaning to a strict XML parser without a DTD defining it -- if entity
			// normalization didn't run, this chapter would XmlException and fall back to raw bytes,
			// leaving its image src unrewritten.
			["OEBPS/ch1.xhtml"] = "<html><body><p>Line one&nbsp;line two</p><img src=\"cover.jpg\"/></body></html>",
		};

		EpubArchive archive = await TestEpubFileBuilder.BuildAsync(entries);
		EpubPublicationInfo publication = EpubPublicationParser.Parse(archive);
		string html = Encoding.UTF8.GetString(CombinedDocumentBuilder.Build(archive, publication));

		Assert.Contains("src=\"../OEBPS/cover.jpg\"", html);
	}
}
