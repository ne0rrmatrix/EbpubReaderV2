using System.Xml.Linq;
using DisplayBook.Viewer.Models;

namespace DisplayBook.Viewer.Services;

/// <summary>
/// Parses an EPUB's container.xml/OPF package document/nav-or-NCX document straight out of an
/// already-open <see cref="EpubArchive"/>, producing everything <see cref="CombinedDocumentBuilder"/>
/// and the reader need to know about the publication's structure. This used to be JavaScript's job
/// (EpubText.js's <c>parsePackage</c>/<c>parseXhtmlToc</c>/<c>parseNcxToc</c>) -- moved to C# so the
/// whole publication can be assembled into one document server-side instead of the WebView
/// fetching/parsing it itself. Follows the same XML-parsing conventions as the (unrelated,
/// import-time-only) <c>EpubPackageReader</c> in DisplayBook.App: <see cref="XDocument"/>,
/// case-insensitive <c>LocalName</c> matching, manual container.xml -&gt; rootfile -&gt; OPF
/// resolution.
/// </summary>
public static class EpubPublicationParser
{
	public static EpubPublicationInfo Parse(EpubArchive archive)
	{
		if (!archive.TryGetEntry("META-INF/container.xml", out byte[] containerBytes))
		{
			throw new InvalidDataException("The EPUB is missing META-INF/container.xml.");
		}

		XDocument container = LoadXml(containerBytes);
		string? opfPath = container.Descendants().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "rootfile", StringComparison.OrdinalIgnoreCase))?.Attribute("full-path")?.Value;
		if (string.IsNullOrWhiteSpace(opfPath))
		{
			throw new InvalidDataException("The EPUB container does not declare a package document.");
		}

		opfPath = EpubPathUtilities.NormalizeEntryPath(opfPath);
		if (!archive.TryGetEntry(opfPath, out byte[] opfBytes))
		{
			throw new InvalidDataException("The EPUB package document could not be found.");
		}

		XDocument package = LoadXml(opfBytes);
		string opfDirectory = EpubPathUtilities.GetEntryDirectory(opfPath);

		XElement? metadataElement = package.Descendants().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "metadata", StringComparison.OrdinalIgnoreCase));

		Dictionary<string, EpubManifestItem> manifestById = new(StringComparer.Ordinal);
		foreach (XElement itemElement in package.Descendants().Where(element =>
			string.Equals(element.Name.LocalName, "item", StringComparison.OrdinalIgnoreCase)))
		{
			string? id = itemElement.Attribute("id")?.Value;
			string? rawHref = itemElement.Attribute("href")?.Value;
			if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(rawHref))
			{
				continue;
			}

			manifestById[id] = new EpubManifestItem(
				id,
				EpubPathUtilities.CombinePath(opfDirectory, rawHref),
				itemElement.Attribute("media-type")?.Value ?? string.Empty,
				itemElement.Attribute("properties")?.Value ?? string.Empty);
		}

		XElement? spineElement = package.Descendants().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "spine", StringComparison.OrdinalIgnoreCase));
		if (spineElement is null)
		{
			throw new InvalidDataException("The EPUB package does not contain a spine.");
		}

		List<EpubSpineItem> spine = [];
		foreach (XElement itemRef in spineElement.Elements().Where(element =>
			string.Equals(element.Name.LocalName, "itemref", StringComparison.OrdinalIgnoreCase)))
		{
			if (string.Equals(itemRef.Attribute("linear")?.Value, "no", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string? idref = itemRef.Attribute("idref")?.Value;
			if (idref is null || !manifestById.TryGetValue(idref, out EpubManifestItem? item))
			{
				continue;
			}

			spine.Add(new EpubSpineItem(item.Href, spine.Count));
		}

		if (spine.Count == 0)
		{
			throw new InvalidDataException("The EPUB package does not contain readable spine resources.");
		}

		Dictionary<string, int> spineIndexByHref = new(StringComparer.OrdinalIgnoreCase);
		foreach (EpubSpineItem spineItem in spine)
		{
			spineIndexByHref.TryAdd(spineItem.Href, spineItem.Index);
		}

		string title = GetMetadataValue(metadataElement, "title", "Untitled publication");
		string author = GetMetadataValue(metadataElement, "creator", string.Empty);

		EpubManifestItem? navXhtmlItem = manifestById.Values.FirstOrDefault(item =>
			item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.OrdinalIgnoreCase));
		string? tocId = spineElement.Attribute("toc")?.Value;
		EpubManifestItem? ncxItem = (tocId is not null && manifestById.TryGetValue(tocId, out EpubManifestItem? byId) ? byId : null)
			?? manifestById.Values.FirstOrDefault(item => string.Equals(item.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase));

		List<EpubTocEntry> toc = [];
		if (navXhtmlItem is not null && archive.TryGetEntry(navXhtmlItem.Href, out byte[] navBytes))
		{
			toc = ParseXhtmlToc(navBytes, navXhtmlItem.Href, spineIndexByHref);
		}
		else if (ncxItem is not null && archive.TryGetEntry(ncxItem.Href, out byte[] ncxBytes))
		{
			toc = ParseNcxToc(ncxBytes, ncxItem.Href, spineIndexByHref);
		}

		string? coverHref = FindCoverHref(metadataElement, manifestById);

		return new EpubPublicationInfo(title, author, spine, toc, manifestById, coverHref);
	}

	static List<EpubTocEntry> ParseXhtmlToc(byte[] navBytes, string navHref, IReadOnlyDictionary<string, int> spineIndexByHref)
	{
		// Matches EpubText.js's parseXhtmlToc exactly: every <a href> in the nav document, not
		// just ones inside a toc-typed <nav> element -- this was never scoped more tightly, and
		// this port isn't the place to start being stricter than the behavior it replaces.
		XDocument navDocument = LoadXml(navBytes);
		string navDirectory = EpubPathUtilities.GetEntryDirectory(navHref);
		List<EpubTocEntry> toc = [];
		foreach (XElement link in navDocument.Descendants().Where(element =>
			string.Equals(element.Name.LocalName, "a", StringComparison.OrdinalIgnoreCase) && element.Attribute("href") is not null))
		{
			string href = link.Attribute("href")!.Value;
			if (!TryResolveTocTarget(href, navDirectory, spineIndexByHref, out int spineIndex, out string fragment))
			{
				continue;
			}

			string label = GetElementText(link);
			toc.Add(new EpubTocEntry(string.IsNullOrWhiteSpace(label) ? $"Section {spineIndex + 1}" : label, spineIndex, fragment));
		}

		return toc;
	}

	static List<EpubTocEntry> ParseNcxToc(byte[] ncxBytes, string ncxHref, IReadOnlyDictionary<string, int> spineIndexByHref)
	{
		XDocument ncxDocument = LoadXml(ncxBytes);
		string ncxDirectory = EpubPathUtilities.GetEntryDirectory(ncxHref);
		List<EpubTocEntry> toc = [];
		foreach (XElement navPoint in ncxDocument.Descendants().Where(element =>
			string.Equals(element.Name.LocalName, "navPoint", StringComparison.OrdinalIgnoreCase)))
		{
			string? href = navPoint.Descendants().FirstOrDefault(element =>
				string.Equals(element.Name.LocalName, "content", StringComparison.OrdinalIgnoreCase))?.Attribute("src")?.Value;
			if (href is null || !TryResolveTocTarget(href, ncxDirectory, spineIndexByHref, out int spineIndex, out string fragment))
			{
				continue;
			}

			string? label = navPoint.Descendants().FirstOrDefault(element =>
				string.Equals(element.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
			toc.Add(new EpubTocEntry(string.IsNullOrWhiteSpace(label) ? $"Section {spineIndex + 1}" : label, spineIndex, fragment));
		}

		return toc;
	}

	static bool TryResolveTocTarget(string href, string baseDirectory, IReadOnlyDictionary<string, int> spineIndexByHref, out int spineIndex, out string fragment)
	{
		spineIndex = -1;
		fragment = string.Empty;
		int fragmentIndex = href.IndexOf('#');
		fragment = fragmentIndex >= 0 ? href[fragmentIndex..] : string.Empty;
		string resolvedHref = EpubPathUtilities.CombinePath(baseDirectory, href);
		return spineIndexByHref.TryGetValue(resolvedHref, out spineIndex);
	}

	static string GetElementText(XElement element) => string.Join(' ', element.DescendantNodesAndSelf()
		.OfType<XText>()
		.Select(text => text.Value.Trim())
		.Where(text => text.Length > 0)).Trim();

	static XDocument LoadXml(byte[] bytes)
	{
		using MemoryStream stream = new(bytes);
		return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
	}

	static string GetMetadataValue(XElement? metadata, string localName, string fallback)
	{
		string? value = metadata?.Elements().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
		return string.IsNullOrWhiteSpace(value) ? fallback : value;
	}

	static string? FindCoverHref(XElement? metadataElement, IReadOnlyDictionary<string, EpubManifestItem> manifestById)
	{
		string? coverId = metadataElement?.Elements().FirstOrDefault(element =>
			string.Equals(element.Name.LocalName, "meta", StringComparison.OrdinalIgnoreCase) &&
			string.Equals(element.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;

		if (coverId is not null && manifestById.TryGetValue(coverId, out EpubManifestItem? byId))
		{
			return byId.Href;
		}

		return manifestById.Values.FirstOrDefault(item =>
			item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("cover-image", StringComparer.OrdinalIgnoreCase))?.Href
			?? manifestById.Values.FirstOrDefault(item => item.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))?.Href;
	}
}
