using System.IO.Compression;
using System.Text;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// Writes small, valid EPUB 3 files. Each call with a different title produces different
/// content, so books imported from them get different content hashes.
/// </summary>
static class TestEpub
{
	// A 1x1 PNG.
	static readonly byte[] coverPng = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

	public static string Write(string title, string author = "Test Author", int chapters = 3, bool withCover = true, string? isbn = null)
	{
		string folder = Path.Combine(FileSystem.CacheDirectory, "TestEpubs");
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, $"{Guid.NewGuid():N}.epub");
		File.WriteAllBytes(path, Build(title, author, chapters, withCover, isbn));
		return path;
	}

	public static byte[] Build(string title, string author = "Test Author", int chapters = 3, bool withCover = true, string? isbn = null)
	{
		using MemoryStream buffer = new();
		using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			Add(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
			Add(zip, "META-INF/container.xml", """
				<?xml version="1.0"?>
				<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
				  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
				</container>
				""");

			StringBuilder manifest = new();
			StringBuilder spine = new();
			StringBuilder nav = new();
			for (int index = 1; index <= chapters; index++)
			{
				manifest.AppendLine($"""<item id="ch{index}" href="Text/ch{index}.xhtml" media-type="application/xhtml+xml"/>""");
				spine.AppendLine($"""<itemref idref="ch{index}"/>""");
				nav.AppendLine($"""<li><a href="Text/ch{index}.xhtml">Chapter {index}</a></li>""");
				Add(zip, $"OEBPS/Text/ch{index}.xhtml", Chapter(title, index));
			}

			if (withCover)
			{
				manifest.AppendLine("""<item id="cover" href="Images/cover.png" media-type="image/png" properties="cover-image"/>""");
				Add(zip, "OEBPS/Images/cover.png", coverPng);
			}

			Add(zip, "OEBPS/nav.xhtml", $"""
				<?xml version="1.0" encoding="utf-8"?>
				<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
				<head><title>Contents</title></head>
				<body><nav epub:type="toc"><ol>{nav}</ol></nav></body>
				</html>
				""");

			// A bare ISBN, not urn:isbn: -- EpubPackageReader only recognises the bare form.
			string identifier = isbn ?? $"urn:uuid:{Guid.NewGuid()}";
			Add(zip, "OEBPS/content.opf", $"""
				<?xml version="1.0" encoding="utf-8"?>
				<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="BookId">
				  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
				    <dc:identifier id="BookId">{identifier}</dc:identifier>
				    <dc:title>{title}</dc:title>
				    <dc:creator>{author}</dc:creator>
				    <dc:language>en</dc:language>
				    <dc:publisher>Test Press</dc:publisher>
				    <dc:description>A book written by the device tests.</dc:description>
				  </metadata>
				  <manifest>
				    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
				    {manifest}
				  </manifest>
				  <spine>
				    {spine}
				  </spine>
				</package>
				""");
		}

		return buffer.ToArray();
	}

	static string Chapter(string title, int index)
	{
		StringBuilder body = new();
		for (int paragraph = 1; paragraph <= 40; paragraph++)
		{
			body.Append($"<p>{title}, chapter {index}, paragraph {paragraph}. ");
			body.Append("The quick brown fox jumps over the lazy dog while the reader turns another page. ");
			body.AppendLine("</p>");
		}

		return $"""
			<?xml version="1.0" encoding="utf-8"?>
			<html xmlns="http://www.w3.org/1999/xhtml">
			<head><title>Chapter {index}</title></head>
			<body><h1 id="top">Chapter {index}</h1>{body}<p><a href="https://example.invalid/">An external link</a></p></body>
			</html>
			""";
	}

	static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal) =>
		Add(zip, name, Encoding.UTF8.GetBytes(content), level);

	static void Add(ZipArchive zip, string name, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
	{
		using Stream stream = zip.CreateEntry(name, level).Open();
		stream.Write(content);
	}
}
