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
        var fullRoot = Path.GetFullPath(bookRoot);
        var containerPath = ResolveWithinRoot(fullRoot, "META-INF/container.xml");
        if (!File.Exists(containerPath))
        {
            throw new InvalidDataException("The selected folder is not an EPUB because META-INF/container.xml is missing.");
        }

        var container = XDocument.Load(containerPath, LoadOptions.PreserveWhitespace);
        var rootfilePath = container.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "rootfile", StringComparison.OrdinalIgnoreCase))?.Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(rootfilePath))
        {
            throw new InvalidDataException("The EPUB container does not declare a package document.");
        }

        var opfPath = ResolveWithinRoot(fullRoot, rootfilePath);
        if (!File.Exists(opfPath))
        {
            throw new InvalidDataException("The EPUB package document could not be found.");
        }

        var package = XDocument.Load(opfPath, LoadOptions.PreserveWhitespace);
        var metadataElement = package.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "metadata", StringComparison.OrdinalIgnoreCase));
        var manifest = package.Descendants().Where(element =>
            string.Equals(element.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase)).ToList();

        var coverId = metadataElement?.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "meta", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string?)element.Attribute("name"), "cover", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
        var coverItem = manifest.FirstOrDefault(item => string.Equals((string?)item.Attribute("id"), coverId, StringComparison.Ordinal))
            ?? manifest.FirstOrDefault(item => ((string?)item.Attribute("properties"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains("cover-image", StringComparer.OrdinalIgnoreCase) == true);

        var coverRelativePath = string.Empty;
        var coverHref = coverItem?.Attribute("href")?.Value;
        if (!string.IsNullOrWhiteSpace(coverHref))
        {
            var coverPath = ResolveWithinRoot(Path.GetDirectoryName(opfPath)!, coverHref);
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

    private static string GetMetadataValue(XElement? metadata, string localName, string fallback)
    {
        var value = metadata?.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>
    /// Best-effort <c>dc:identifier</c> ISBN lookup: an element with an
    /// <c>opf:scheme="ISBN"</c> attribute wins first, otherwise any
    /// identifier whose text validates as an ISBN-10/13. Never throws;
    /// returns "" when nothing qualifies.
    /// </summary>
    private static string ExtractIsbn(XElement? metadata)
    {
        if (metadata is null)
        {
            return string.Empty;
        }

        var identifierElements = metadata.Elements()
            .Where(element => string.Equals(element.Name.LocalName, "identifier", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var schemeIsbnElements = identifierElements.Where(element => string.Equals(
            element.Attributes().FirstOrDefault(attr => string.Equals(attr.Name.LocalName, "scheme", StringComparison.OrdinalIgnoreCase))?.Value,
            "ISBN",
            StringComparison.OrdinalIgnoreCase));

        return FirstValidIsbn(schemeIsbnElements) ?? FirstValidIsbn(identifierElements) ?? string.Empty;
    }

    private static string? FirstValidIsbn(IEnumerable<XElement> elements)
    {
        foreach (var element in elements)
        {
            var candidate = BookIdentifiers.Clean(element.Value);
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
        var normalizedRoot = Path.GetFullPath(root);
        var candidate = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, candidate));
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The EPUB contains a path outside its publication directory.");
        }

        return fullPath;
    }
}
