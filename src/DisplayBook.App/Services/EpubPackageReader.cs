using System.Xml.Linq;
using DisplayBook.App.Services.BookMetadata;

namespace DisplayBook.App.Services;

public sealed record EpubPackageMetadata(
	string Title,
	string Author,
	string Description,
	string Language,
	string Publisher,
	string OpfRelativePath,
	string CoverRelativePath,
	string Isbn);

public static class EpubPackageReader
{
	public static EpubPackageMetadata Read(string bookRoot)
	{
		string fullRoot = Path.GetFullPath(bookRoot);
		string containerPath = ResolveWithinRoot(fullRoot, "META-INF/container.xml");
		if (!File.Exists(containerPath))
		{
			throw new InvalidDataException("The selected folder is not an EPUB because META-INF/container.xml is missing.");
		}

		XDocument container = XDocument.Load(containerPath, LoadOptions.PreserveWhitespace);
		string? rootfilePath = container.Descendants().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "rootfile", StringComparison.OrdinalIgnoreCase))?.Attribute("full-path")?.Value;
		if (string.IsNullOrWhiteSpace(rootfilePath))
		{
			throw new InvalidDataException("The EPUB container does not declare a package document.");
		}

		string opfPath = ResolveWithinRoot(fullRoot, rootfilePath);
		if (!File.Exists(opfPath))
		{
			throw new InvalidDataException("The EPUB package document could not be found.");
		}

		XDocument package = XDocument.Load(opfPath, LoadOptions.PreserveWhitespace);
		XElement? metadataElement = package.Descendants().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "metadata", StringComparison.OrdinalIgnoreCase));
		List<XElement> manifest = package.Descendants().Where(element =>
			string.Equals(element.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase)).ToList();

		string? coverId = metadataElement?.Elements().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "meta", StringComparison.OrdinalIgnoreCase) &&
			string.Equals((string?)element.Attribute("name"), "cover", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
		XElement? coverItem = manifest.FirstOrDefault(item => string.Equals((string?)item.Attribute("id"), coverId, StringComparison.Ordinal))
			?? manifest.FirstOrDefault(item => ((string?)item.Attribute("properties"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Contains("cover-image", StringComparer.OrdinalIgnoreCase) == true);

		string coverRelativePath = string.Empty;
		string? coverHref = coverItem?.Attribute("href")?.Value;
		if (!string.IsNullOrWhiteSpace(coverHref))
		{
			string coverPath = ResolveWithinRoot(Path.GetDirectoryName(opfPath)!, coverHref);
			if (File.Exists(coverPath))
			{
				coverRelativePath = Path.GetRelativePath(fullRoot, coverPath).Replace('\\', '/');
			}
		}

		return new EpubPackageMetadata(
			GetMetadataValue(metadataElement, "title", "Untitled book"),
			GetMetadataValue(metadataElement, "creator", "Unknown author"),
			GetMetadataValue(metadataElement, "description", string.Empty),
			GetMetadataValue(metadataElement, "language", string.Empty),
			GetMetadataValue(metadataElement, "publisher", string.Empty),
			Path.GetRelativePath(fullRoot, opfPath).Replace('\\', '/'),
			coverRelativePath,
			ExtractIsbn(metadataElement));
	}

	static string GetMetadataValue(XElement? metadata, string localName, string fallback)
	{
		string? value = metadata?.Elements().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
		return string.IsNullOrWhiteSpace(value) ? fallback : value;
	}

	/// <summary>
	/// Best-effort <c>dc:identifier</c> ISBN lookup: an element with an
	/// <c>opf:scheme="ISBN"</c> attribute wins first, otherwise any
	/// identifier whose text validates as an ISBN-10/13. Never throws;
	/// returns "" when nothing qualifies.
	/// </summary>
	static string ExtractIsbn(XElement? metadata)
	{
		if (metadata is null)
		{
			return string.Empty;
		}

		List<XElement> identifierElements = metadata.Elements()
			.Where(element => string.Equals(element.Name.LocalName, "identifier", StringComparison.OrdinalIgnoreCase))
			.ToList();

		IEnumerable<XElement> schemeIsbnElements = identifierElements.Where(element => string.Equals(
			element.Attributes().FirstOrDefault(attr => string.Equals(attr.Name.LocalName, "scheme", StringComparison.OrdinalIgnoreCase))?.Value,
			"ISBN",
			StringComparison.OrdinalIgnoreCase));

		return FirstValidIsbn(schemeIsbnElements) ?? FirstValidIsbn(identifierElements) ?? string.Empty;
	}

	static string? FirstValidIsbn(IEnumerable<XElement> elements)
	{
		foreach (XElement element in elements)
		{
			string candidate = BookIdentifiers.Clean(element.Value);
			if (candidate.Length > 0 &&
				BookIdentifiers.Classify(candidate) is BookIdentifierKind.Isbn10 or BookIdentifierKind.Isbn13)
			{
				return candidate;
			}
		}

		return null;
	}

	internal static string ResolveWithinRoot(string root, string relativePath)
	{
		string normalizedRoot = Path.GetFullPath(root);
		string candidate = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
		string fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, candidate));
		string rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
			? normalizedRoot
			: normalizedRoot + Path.DirectorySeparatorChar;
		return !fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
			? throw new InvalidDataException("The EPUB contains a path outside its publication directory.")
			: fullPath;
	}
}
