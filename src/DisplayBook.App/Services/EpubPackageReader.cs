using System.IO.Compression;
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
	public static EpubPackageMetadata Read(ZipArchive archive)
	{
		ZipArchiveEntry containerEntry = FindEntry(archive, "META-INF/container.xml")
			?? throw new InvalidDataException("The selected file is not an EPUB because META-INF/container.xml is missing.");

		XDocument container = LoadXml(containerEntry);
		string? rootfilePath = container.Descendants().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "rootfile", StringComparison.OrdinalIgnoreCase))?.Attribute("full-path")?.Value;
		if (string.IsNullOrWhiteSpace(rootfilePath))
		{
			throw new InvalidDataException("The EPUB container does not declare a package document.");
		}

		ZipArchiveEntry opfEntry = FindEntry(archive, rootfilePath)
			?? throw new InvalidDataException("The EPUB package document could not be found.");
		string opfPath = NormalizeEntryPath(opfEntry.FullName);

		XDocument package = LoadXml(opfEntry);
		XElement? metadataElement = package.Descendants().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "metadata", StringComparison.OrdinalIgnoreCase));
		List<XElement> manifest = [.. package.Descendants().Where(element =>
			string.Equals(element.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase))];

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
			string opfDirectory = GetEntryDirectory(opfPath);
			ZipArchiveEntry? coverEntry = FindEntry(archive, CombineEntryPath(opfDirectory, coverHref));
			if (coverEntry is not null)
			{
				coverRelativePath = NormalizeEntryPath(coverEntry.FullName);
			}
		}

		return new EpubPackageMetadata(
			GetMetadataValue(metadataElement, "title", "Untitled book"),
			GetMetadataValue(metadataElement, "creator", "Unknown author"),
			GetMetadataValue(metadataElement, "description", string.Empty),
			GetMetadataValue(metadataElement, "language", string.Empty),
			GetMetadataValue(metadataElement, "publisher", string.Empty),
			opfPath,
			coverRelativePath,
			ExtractIsbn(metadataElement));
	}

	static XDocument LoadXml(ZipArchiveEntry entry)
	{
		using Stream stream = entry.Open();
		return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
	}

	/// <summary>
	/// Zip entry names are technically case-sensitive, but real-world EPUBs occasionally
	/// disagree in case between container.xml/OPF references and the actual entry -- this was
	/// silently tolerated before by NTFS/case-insensitive disk lookups, so an exact match is
	/// tried first and a case-insensitive scan is the fallback rather than a hard failure.
	/// </summary>
	static ZipArchiveEntry? FindEntry(ZipArchive archive, string entryPath)
	{
		string normalized = NormalizeEntryPath(entryPath);
		return archive.GetEntry(normalized)
			?? archive.Entries.FirstOrDefault(entry => string.Equals(NormalizeEntryPath(entry.FullName), normalized, StringComparison.OrdinalIgnoreCase));
	}

	static string NormalizeEntryPath(string path) => path.Replace('\\', '/').TrimStart('/');

	static string GetEntryDirectory(string entryPath)
	{
		int lastSlash = entryPath.LastIndexOf('/');
		return lastSlash < 0 ? string.Empty : entryPath[..lastSlash];
	}

	static string CombineEntryPath(string baseDirectory, string relativePath)
	{
		string combined = string.IsNullOrEmpty(baseDirectory) ? relativePath : $"{baseDirectory}/{relativePath}";
		List<string> resolved = [];
		foreach (string segment in combined.Replace('\\', '/').Split('/'))
		{
			switch (segment)
			{
				case "" or ".":
					continue;
				case "..":
					if (resolved.Count > 0)
					{
						resolved.RemoveAt(resolved.Count - 1);
					}

					continue;
				default:
					resolved.Add(segment);
					break;
			}
		}

		return string.Join('/', resolved);
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

		List<XElement> identifierElements = [.. metadata.Elements().Where(element => string.Equals(element.Name.LocalName, "identifier", StringComparison.OrdinalIgnoreCase))];

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

	/// <summary>
	/// Disk-path-traversal guard, kept for callers combining an untrusted relative path (e.g. a
	/// display name from an Android SAF content provider -- see BookPickerService.android.cs)
	/// against a trusted local folder. Not used by EPUB archive-entry resolution above, since a
	/// zip entry name can't "escape" the archive it's looked up in.
	/// </summary>
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
			? throw new InvalidDataException("The path escapes its expected directory.")
			: fullPath;
	}
}
