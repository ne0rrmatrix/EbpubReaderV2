namespace DisplayBook.Viewer.Models;

/// <summary>
/// Everything <c>window.DisplayBookReader.loadPublication(...)</c> needs, handed across the
/// native-to-JS boundary as one JSON argument (see <c>EpubReaderView.SendLoadPublicationAsync</c>)
/// -- replacing the bare opf-path string the reader used to send before JS parsed the OPF/TOC
/// itself. Now that parsing happens once in C# (<see cref="DisplayBook.Viewer.Services.EpubPublicationParser"/>),
/// JS never re-derives <see cref="Spine"/>/<see cref="Toc"/> and never fetches the OPF or nav
/// document at all.
/// </summary>
/// <param name="CombinedHref">
/// The book-relative URL of the single HTML document containing every chapter (see
/// <see cref="DisplayBook.Viewer.Services.CombinedDocumentBuilder"/>), in the same "../"-escaped
/// form <c>EpubReaderView</c> already uses for opf paths so it resolves correctly against the
/// reader shell's own URL.
/// </param>
/// <param name="Title">The publication's title.</param>
/// <param name="Author">The publication's author, or empty if none was declared.</param>
/// <param name="Spine">The reading order, in spine order.</param>
/// <param name="Toc">The flattened table of contents, in document order.</param>
/// <param name="CoverHref">
/// The book-relative URL (same "../" escape as <paramref name="CombinedHref"/>) of the cover
/// image, or <c>null</c> if the publication doesn't declare one -- used only for the JS-rendered
/// loading-screen cover preview, not the native one (see <c>EpubReaderView.CoverImageSource</c>).
/// </param>
public sealed record ReaderPublicationPayload(
	string CombinedHref,
	string Title,
	string Author,
	IReadOnlyList<EpubSpineItem> Spine,
	IReadOnlyList<EpubTocEntry> Toc,
	string? CoverHref);
