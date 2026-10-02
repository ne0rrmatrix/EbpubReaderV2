namespace DisplayBook.Viewer.Services;

/// <summary>
/// Relative-path resolution shared by <see cref="EpubPublicationParser"/> (manifest hrefs against
/// the OPF's own directory) and <see cref="CombinedDocumentBuilder"/> (each chapter's asset hrefs
/// against that chapter's own directory) -- both need the exact same "resolve a relative EPUB
/// path against a base directory inside the zip" logic, so it lives in one place rather than
/// being reimplemented twice.
/// </summary>
static class EpubPathUtilities
{
	internal static string NormalizeEntryPath(string path) => path.Replace('\\', '/').TrimStart('/');

	/// <summary>
	/// The viewer shell (index.html/EpubText.js) lives under "DisplayBookViewer/"; the "../"
	/// escapes back to the root that the currently-open book's own content -- including the
	/// combined document <see cref="CombinedDocumentBuilder"/> assembles, which is served from the
	/// same nesting depth as the shell itself -- is served from. Used by
	/// <see cref="ReaderAssetHost"/> for the (legacy, opf-in-query-string) viewer URL and the JSON
	/// payload handed to <c>window.DisplayBookReader.loadPublication</c>, and by
	/// <see cref="CombinedDocumentBuilder"/> for every asset reference it rewrites -- all need this
	/// same "../" escape, or a request resolves to "DisplayBookViewer/&lt;path&gt;" instead of the
	/// book's own root and every resource request 404s.
	/// </summary>
	internal static string GetBookRelativePath(string epubRelativePath) => $"../{epubRelativePath.Replace('\\', '/').Trim('/')}";

	internal static string GetEntryDirectory(string entryPath)
	{
		int lastSlash = entryPath.LastIndexOf('/');
		return lastSlash < 0 ? string.Empty : entryPath[..lastSlash];
	}

	/// <summary>
	/// Resolves <paramref name="relativePath"/> (an href as it appears in the OPF manifest or a
	/// chapter's own markup, which may contain "./"/"../" segments) against
	/// <paramref name="baseDirectory"/> (the zip-entry directory the href is relative to), and
	/// strips any query/fragment first since those aren't part of the zip entry's path.
	/// </summary>
	internal static string CombinePath(string baseDirectory, string relativePath)
	{
		string withoutFragment = relativePath;
		int fragmentIndex = withoutFragment.IndexOfAny(['?', '#']);
		if (fragmentIndex >= 0)
		{
			withoutFragment = withoutFragment[..fragmentIndex];
		}

		string combined = string.IsNullOrEmpty(baseDirectory) ? withoutFragment : $"{baseDirectory}/{withoutFragment}";
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
					resolved.Add(Uri.UnescapeDataString(segment));
					break;
			}
		}

		return string.Join('/', resolved);
	}
}
