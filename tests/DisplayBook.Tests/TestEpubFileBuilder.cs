using System.IO.Compression;
using DisplayBook.Viewer.Services;

namespace DisplayBook.Tests;

/// <summary>
/// Builds a real, temporary .epub file from a set of entries and opens it as an
/// <see cref="EpubArchive"/> -- needed because <see cref="EpubArchive.OpenAsync"/> reads from a
/// file path, not an in-memory stream (unlike the ZipArchive-based helper
/// <c>EpubPackageReaderTests</c> uses for the unrelated import-time reader).
/// </summary>
static class TestEpubFileBuilder
{
	public static async Task<EpubArchive> BuildAsync(IReadOnlyDictionary<string, string> textEntries)
	{
		string path = Path.Combine(Path.GetTempPath(), $"displaybook-test-{Guid.NewGuid():N}.epub");
		using (FileStream fileStream = new(path, FileMode.Create, FileAccess.Write))
		using (ZipArchive archive = new(fileStream, ZipArchiveMode.Create, leaveOpen: false))
		{
			foreach ((string entryName, string content) in textEntries)
			{
				ZipArchiveEntry entry = archive.CreateEntry(entryName);
				using Stream entryStream = entry.Open();
				using StreamWriter writer = new(entryStream);
				writer.Write(content);
			}
		}

		try
		{
			return await EpubArchive.OpenAsync(path);
		}
		finally
		{
			File.Delete(path);
		}
	}
}
