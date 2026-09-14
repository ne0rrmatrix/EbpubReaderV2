using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DisplayBook.Viewer.Models;

namespace DisplayBook.Viewer.Services;

/// <summary>
/// Assembles every spine chapter of a publication into ONE HTML document, each wrapped in a
/// hidden <c>&lt;section data-chapter-index="N"&gt;</c>, with every book-CSS file inlined and
/// every chapter's image/font/stylesheet references rewritten to resolve against the combined
/// document's own location instead of the chapter's original one. This replaces EpubText.js's old
/// per-chapter <c>fetch()</c>/<c>createCachedResource</c> path -- the WebView is handed one
/// already-complete document instead of building the book up one chapter at a time itself.
/// </summary>
public static partial class CombinedDocumentBuilder
{
	/// <summary>
	/// Reserved synthetic path the combined document is stored under via
	/// <see cref="EpubArchive.SetSyntheticEntry"/> -- placed at the same nesting depth as the
	/// reader shell's own "DisplayBookViewer/index.html" (one level under the book's own root) so
	/// references from the combined document back into real book resources need exactly the same
	/// single "../" escape <see cref="EpubPathUtilities.GetBookRelativePath"/> already applies for
	/// opf paths. Chosen to be exceedingly unlikely to collide with any real EPUB-internal path.
	/// </summary>
	public const string CombinedDocumentPath = "__displaybook_reader__/combined.html";

	[SuppressMessage("Security", "S1075", Justification = "Standard external namespace URI convention.")]
	const string xlinkNamespaceUri = "http://www.w3.org/1999/xlink";
	static readonly XName xlinkHrefName = XName.Get("href", xlinkNamespaceUri);

	[GeneratedRegex(@"url\(\s*(['""]?)([^'"")]+)\1\s*\)", RegexOptions.IgnoreCase)]
	private static partial Regex CssUrlPattern();

	// Real-world EPUB chapters routinely use HTML named entities (&nbsp;, &mdash;, &eacute;, ...)
	// that plain XML doesn't recognize without a DTD defining them -- and DTD resolution is
	// deliberately disabled below (DtdProcessing.Ignore) rather than fetched. Expanding them to
	// their literal characters first (reusing WebUtility's own entity table, so there's no
	// hand-maintained list to keep in sync) avoids the raw-bytes fallback firing on this extremely
	// common case; the 5 XML-builtin entities are left alone since they're already valid as-is.
	[GeneratedRegex(@"&([a-zA-Z][a-zA-Z0-9]{1,31});")]
	private static partial Regex NamedEntityPattern();

	public static byte[] Build(EpubArchive archive, EpubPublicationInfo publication)
	{
		StringBuilder html = new();
		html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
		html.Append("<link rel=\"stylesheet\" href=\"../DisplayBookViewer/ReadiumCSS-before.css\">");
		html.Append("<link rel=\"stylesheet\" href=\"../DisplayBookViewer/ReadiumCSS-default.css\">");
		html.Append("<link rel=\"stylesheet\" href=\"../DisplayBookViewer/ReadiumCSS-after.css\">");

		foreach (string cssHref in publication.ManifestById.Values
			.Where(static item => string.Equals(item.MediaType, "text/css", StringComparison.OrdinalIgnoreCase))
			.Select(static item => item.Href))
		{
			if (!archive.TryGetEntry(cssHref, out byte[] cssBytes))
			{
				continue;
			}

			string cssDirectory = EpubPathUtilities.GetEntryDirectory(cssHref);
			string rewrittenCss = RewriteCssUrls(Encoding.UTF8.GetString(cssBytes), cssDirectory);
			html.Append("<style>").Append(rewrittenCss).Append("</style>");
		}

		html.Append("<style>section[data-chapter-index]{display:none}</style>");
		html.Append("</head><body>");

		for (int index = 0; index < publication.Spine.Count; index++)
		{
			EpubSpineItem spineItem = publication.Spine[index];
			html.Append("<section id=\"chapter-").Append(index)
				.Append("\" data-chapter-index=\"").Append(index)
				.Append("\" data-chapter-href=\"").Append(WebUtility.HtmlEncode(spineItem.Href))
				.Append("\">");
			html.Append(BuildChapterBody(archive, spineItem.Href));
			html.Append("</section>");
		}

		html.Append("</body></html>");
		return Encoding.UTF8.GetBytes(html.ToString());
	}

	static string BuildChapterBody(EpubArchive archive, string chapterHref)
	{
		if (!archive.TryGetEntry(chapterHref, out byte[] chapterBytes))
		{
			return string.Empty;
		}

		string chapterText = Encoding.UTF8.GetString(chapterBytes);
		try
		{
			string chapterDirectory = EpubPathUtilities.GetEntryDirectory(chapterHref);
			XDocument chapterDocument = ParseLeniently(chapterText);
			XElement? body = chapterDocument.Descendants().FirstOrDefault(static element =>
				string.Equals(element.Name.LocalName, "body", StringComparison.OrdinalIgnoreCase));
			if (body is null)
			{
				return chapterText;
			}

			RewriteAssetReferences(body, chapterDirectory);
			return string.Concat(body.Nodes().Select(static node => node.ToString(SaveOptions.DisableFormatting)));
		}
		catch (Exception exception) when (exception is XmlException or InvalidOperationException)
		{
			// Not every real-world EPUB chapter is strictly well-formed XHTML the way the
			// browser's own lenient HTML parser tolerated it before this rewrite. Falling back to
			// the chapter's raw, unrewritten bytes keeps the rest of the book working instead of
			// failing the whole publication over one bad chapter; the WebView still parses the
			// overall combined document as lenient HTML (see ReaderAssetHost's `.html`, not
			// `.xhtml`, extension for it), so this chapter still renders -- just with any relative
			// asset references left pointing at the wrong (chapter-relative, not
			// combined-document-relative) location.
			return chapterText;
		}
	}

	/// <summary>
	/// Parses XHTML that may have a DOCTYPE declaration (DTD resolution is disabled entirely
	/// rather than fetched -- this reader never needs network access to render a book) and/or
	/// undeclared HTML named entities (expanded to literal characters first).
	/// </summary>
	static XDocument ParseLeniently(string xhtmlText)
	{
		string normalized = NamedEntityPattern().Replace(xhtmlText, static match =>
		{
			string name = match.Groups[1].Value;
			if (name is "amp" or "lt" or "gt" or "quot" or "apos")
			{
				return match.Value;
			}

			string decoded = WebUtility.HtmlDecode(match.Value);
			return string.Equals(decoded, match.Value, StringComparison.Ordinal) ? match.Value : decoded;
		});

		XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore };
		using StringReader stringReader = new(normalized);
		using XmlReader xmlReader = XmlReader.Create(stringReader, settings);
		return XDocument.Load(xmlReader, LoadOptions.None);
	}

	static void RewriteAssetReferences(XElement root, string baseDirectory)
	{
		foreach (XElement element in root.DescendantsAndSelf())
		{
			RewriteAttribute(element, "src", baseDirectory);
			RewriteAttribute(element, xlinkHrefName, baseDirectory);
			if (string.Equals(element.Name.LocalName, "image", StringComparison.OrdinalIgnoreCase))
			{
				RewriteAttribute(element, "href", baseDirectory);
			}

			XAttribute? styleAttribute = element.Attribute("style");
			if (styleAttribute is not null && styleAttribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase))
			{
				styleAttribute.Value = RewriteCssUrls(styleAttribute.Value, baseDirectory);
			}
		}
	}

	static void RewriteAttribute(XElement element, XName attributeName, string baseDirectory)
	{
		XAttribute? attribute = element.Attribute(attributeName);
		if (attribute is null || IsAbsoluteOrDataUrl(attribute.Value))
		{
			return;
		}

		attribute.Value = EpubPathUtilities.GetBookRelativePath(EpubPathUtilities.CombinePath(baseDirectory, attribute.Value));
	}

	static string RewriteCssUrls(string cssText, string baseDirectory)
	{
		return CssUrlPattern().Replace(cssText, match =>
		{
			string rawValue = match.Groups[2].Value.Trim();
			if (IsAbsoluteOrDataUrl(rawValue))
			{
				return match.Value;
			}

			string rewritten = EpubPathUtilities.GetBookRelativePath(EpubPathUtilities.CombinePath(baseDirectory, rawValue));
			return $"url(\"{rewritten}\")";
		});
	}

	static bool IsAbsoluteOrDataUrl(string value) =>
		value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
		value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
		value.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
		value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
		value.StartsWith("//", StringComparison.Ordinal) ||
		value.StartsWith("#", StringComparison.Ordinal);
}
