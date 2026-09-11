using DisplayBook.App.Services;
using Xunit;

namespace DisplayBook.Tests;

public class EpubPackageReaderTests
{
	[Fact]
	public void Read_IdentifierWithIsbnScheme_ExtractsIsbn()
	{
		string root = CreateEpubRoot("""<dc:identifier id="BookId" opf:scheme="ISBN">9780132350884</dc:identifier>""");
		try
		{
			EpubPackageMetadata metadata = EpubPackageReader.Read(root);
			Assert.Equal("9780132350884", metadata.Isbn);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Read_IdentifierWithoutScheme_BestEffortAcceptsValidIsbn13()
	{
		string root = CreateEpubRoot("""<dc:identifier id="BookId">9780132350884</dc:identifier>""");
		try
		{
			EpubPackageMetadata metadata = EpubPackageReader.Read(root);
			Assert.Equal("9780132350884", metadata.Isbn);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Read_IdentifierIsNonIsbnUrn_LeavesIsbnEmpty()
	{
		string root = CreateEpubRoot("""<dc:identifier id="BookId">urn:uuid:12345678-1234-1234-1234-123456789012</dc:identifier>""");
		try
		{
			EpubPackageMetadata metadata = EpubPackageReader.Read(root);
			Assert.Equal(string.Empty, metadata.Isbn);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Read_NoIdentifierElement_LeavesIsbnEmpty()
	{
		string root = CreateEpubRoot(string.Empty);
		try
		{
			EpubPackageMetadata metadata = EpubPackageReader.Read(root);
			Assert.Equal(string.Empty, metadata.Isbn);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Read_SchemeIdentifierPreferredOverNonSchemeInvalidOne()
	{
		string root = CreateEpubRoot("""
            <dc:identifier id="uuid">urn:uuid:12345678-1234-1234-1234-123456789012</dc:identifier>
            <dc:identifier id="isbn" opf:scheme="ISBN">0132350882</dc:identifier>
            """);
		try
		{
			EpubPackageMetadata metadata = EpubPackageReader.Read(root);
			Assert.Equal("0132350882", metadata.Isbn);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	static string CreateEpubRoot(string identifierElements)
	{
		string root = Path.Combine(Path.GetTempPath(), "DisplayBookTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(root, "META-INF"));
		File.WriteAllText(Path.Combine(root, "META-INF", "container.xml"), """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);

		File.WriteAllText(Path.Combine(root, "content.opf"), $"""
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

		return root;
	}
}
