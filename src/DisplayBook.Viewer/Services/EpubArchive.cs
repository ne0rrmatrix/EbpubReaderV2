using System.IO.Compression;

namespace DisplayBook.Viewer.Services;

/// <summary>
/// A publication's content, fully read into memory once from its persisted <c>.epub</c> file so
/// that reading never touches disk again per-resource. Every platform's WebView resource handler
/// (<see cref="ReaderAssetHost"/> and its platform partials) looks up chapter/CSS/image/font
/// bytes here instead of opening individual files -- avoiding both the per-file disk I/O and the
/// real-time antivirus scan that comes with it.
/// </summary>
public sealed class EpubArchive
{
	readonly Dictionary<string, byte[]> entriesByPath;

	EpubArchive(Dictionary<string, byte[]> entriesByPath)
	{
		this.entriesByPath = entriesByPath;
	}

	public static async Task<EpubArchive> OpenAsync(string epubFilePath, CancellationToken cancellationToken = default)
	{
		Dictionary<string, byte[]> entries = new(StringComparer.OrdinalIgnoreCase);
		await using (FileStream fileStream = new(
			epubFilePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			useAsync: true))
		using (ZipArchive archive = new(fileStream, ZipArchiveMode.Read, leaveOpen: false))
		{
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();

				// A directory entry (e.g. "OEBPS/") has an empty Name -- nothing to read.
				if (string.IsNullOrEmpty(entry.Name))
				{
					continue;
				}

				using Stream entryStream = await entry.OpenAsync(cancellationToken);
				using MemoryStream buffer = new(checked((int)entry.Length));
				await entryStream.CopyToAsync(buffer, cancellationToken);
				entries[NormalizePath(entry.FullName)] = buffer.ToArray();
			}
		}

		return new EpubArchive(entries);
	}

	/// <summary>
	/// Looks up a resource by request path (as received from a WebView resource request --
	/// scheme/host/query/fragment already stripped by the caller, but not yet unescaped or
	/// normalized).
	/// </summary>
	public bool TryGetEntry(string requestPath, out byte[] bytes)
	{
		string normalized = NormalizeRequestPath(requestPath);
		return entriesByPath.TryGetValue(normalized, out bytes!);
	}

	/// <summary>
	/// Adds (or replaces) an entry that didn't come from the original zip -- specifically, the
	/// single combined HTML document <see cref="CombinedDocumentBuilder"/> assembles from every
	/// spine chapter. Once set, it's indistinguishable from a real archive entry to every
	/// platform's resource handler (all of them go through <see cref="TryGetEntry"/>), so nothing
	/// downstream needs to know the bytes weren't actually in the .epub file. <paramref
	/// name="virtualPath"/> must not collide with a real EPUB-internal path -- see
	/// <see cref="CombinedDocumentBuilder.CombinedDocumentPath"/>'s reserved-path comment.
	/// </summary>
	internal void SetSyntheticEntry(string virtualPath, byte[] bytes)
	{
		entriesByPath[NormalizePath(virtualPath)] = bytes;
	}

	static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

	/// <summary>
	/// Strips query/fragment and unescapes a WebView resource request path down to a plain
	/// relative path. Shared with <see cref="ReaderAssetHost"/>'s viewer-shell lookup so both
	/// sources of resource bytes agree on the same normalization.
	/// </summary>
	internal static string NormalizeRequestPath(string requestPath)
	{
		string path = requestPath;
		int queryIndex = path.IndexOfAny(['?', '#']);
		if (queryIndex >= 0)
		{
			path = path[..queryIndex];
		}

		try
		{
			path = Uri.UnescapeDataString(path);
		}
		catch (UriFormatException)
		{
			// Leave the path as-is; the subsequent lookup will simply miss.
		}

		return NormalizePath(path);
	}
}
