namespace DisplayBook.Viewer.Models;

/// <summary>A single OPF manifest item, keyed by id in <see cref="EpubPublicationInfo.ManifestById"/>.</summary>
/// <param name="Id">The manifest item's <c>id</c> attribute.</param>
/// <param name="Href">The item's href, resolved to a zip-entry-relative path (not the raw, possibly-relative-to-the-OPF-directory value from the manifest).</param>
/// <param name="MediaType">The item's <c>media-type</c> attribute.</param>
/// <param name="Properties">The item's <c>properties</c> attribute (space-separated tokens), or empty.</param>
public sealed record EpubManifestItem(string Id, string Href, string MediaType, string Properties);

/// <summary>
/// One entry in the reading order. <see cref="Href"/> is the same zip-entry-relative path format
/// already persisted as <see cref="EpubLocator.ResourceHref"/> -- this type must never diverge
/// from that format, since it's the cross-device sync key.
/// </summary>
public sealed record EpubSpineItem(string Href, int Index);

/// <summary>A flattened table-of-contents entry (matching the flat, non-hierarchical TOC the reader has always shown).</summary>
public sealed record EpubTocEntry(string Label, int SpineIndex, string Fragment);

/// <summary>
/// The result of parsing an EPUB's container.xml/OPF/nav-or-NCX document (see
/// <see cref="DisplayBook.Viewer.Services.EpubPublicationParser"/>) -- everything the reader needs
/// to know about a publication's structure before it can build the combined reading document or
/// resolve a locator.
/// </summary>
public sealed record EpubPublicationInfo(
	string Title,
	string Author,
	IReadOnlyList<EpubSpineItem> Spine,
	IReadOnlyList<EpubTocEntry> Toc,
	IReadOnlyDictionary<string, EpubManifestItem> ManifestById,
	string? CoverHref);
