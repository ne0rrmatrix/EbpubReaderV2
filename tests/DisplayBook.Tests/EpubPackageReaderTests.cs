using System.IO.Compression;
using DisplayBook.App.Services;
using Xunit;

namespace DisplayBook.Tests;

public class EpubPackageReaderTests
{
	[Fact]
	public void Read_IdentifierWithIsbnScheme_ExtractsIsbn()
	{
		using ZipArchive archive = CreateEpubArchive("""<dc:identifier id="BookId" opf:scheme="ISBN">9780132350884</dc:identifier>""");
		EpubPackageMetadata metadata = EpubPackageReader.Read(archive);
		Assert.Equal("9780132350884", metadata.Isbn);
	}

	[Fact]
	public void Read_IdentifierWithoutScheme_BestEffortAcceptsValidIsbn13()
	{
		using ZipArchive archive = CreateEpubArchive("""<dc:identifier id="BookId">9780132350884</dc:identifier>""");
		EpubPackageMetadata metadata = EpubPackageReader.Read(archive);
		Assert.Equal("9780132350884", metadata.Isbn);
	}

	[Fact]
	public void Read_IdentifierIsNonIsbnUrn_LeavesIsbnEmpty()
	{
		using ZipArchive archive = CreateEpubArchive("""<dc:identifier id="BookId">urn:uuid:12345678-1234-1234-1234-123456789012</dc:identifier>""");
		EpubPackageMetadata metadata = EpubPackageReader.Read(archive);
		Assert.Equal(string.Empty, metadata.Isbn);
	}

	[Fact]
	public void Read_NoIdentifierElement_LeavesIsbnEmpty()
	{
		using ZipArchive archive = CreateEpubArchive(string.Empty);
		EpubPackageMetadata metadata = EpubPackageReader.Read(archive);
		Assert.Equal(string.Empty, metadata.Isbn);
	}

	[Fact]
	public void Read_SchemeIdentifierPreferredOverNonSchemeInvalidOne()
	{
		using ZipArchive archive = CreateEpubArchive("""
            <dc:identifier id="uuid">urn:uuid:12345678-1234-1234-1234-123456789012</dc:identifier>
            <dc:identifier id="isbn" opf:scheme="ISBN">0132350882</dc:identifier>
            """);
		EpubPackageMetadata metadata = EpubPackageReader.Read(archive);
		Assert.Equal("0132350882", metadata.Isbn);
	}

	static ZipArchive CreateEpubArchive(string identifierElements)
	{
		MemoryStream stream = new();
		using (ZipArchive writer = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			WriteEntry(writer, "META-INF/container.xml", """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);

			WriteEntry(writer, "content.opf", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="BookId">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
                <dc:title>Test Book</dc:title>
                <dc:creator>Test Author</dc:creator>
                {identifierElements}
              </metadata>
              <manifest></manifest>
              <spine></spine>
            </package>
            """);
		}

		stream.Position = 0;
		return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
	}

	static void WriteEntry(ZipArchive archive, string entryName, string content)
	{
		ZipArchiveEntry entry = archive.CreateEntry(entryName);
		using Stream entryStream = entry.Open();
		using StreamWriter writer = new(entryStream);
		writer.Write(content);
	}
}
