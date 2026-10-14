namespace DisplayBook.App.Services;

public static class BookStorageService
{
	public static string ContentRoot => Path.Combine(FileSystem.AppDataDirectory, "ReaderContent");

	public static string BooksRoot => Path.Combine(BookStorageService.ContentRoot, "Books");

	public static string CoversRoot => Path.Combine(BookStorageService.ContentRoot, "Covers");

	public static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "displaybook.db");

	/// <summary>
	/// The persisted original <c>.epub</c> file for a book -- the sole on-disk copy of its
	/// content. Everything else (chapters, CSS, images, fonts) is parsed into memory fresh each
	/// time the book is opened for reading (see <c>EpubArchive</c>), never extracted to disk.
	/// </summary>
	public static string GetBookFilePath(string bookId) => Path.Combine(BookStorageService.BooksRoot, $"{bookId}.epub");

	/// <summary>
	/// A small, separately-persisted cover image extracted once at import time -- the one piece
	/// of book content that's still cached on disk, since the library/details pages bind a plain
	/// file-path <c>ImageSource</c> to it.
	/// </summary>
	public static string GetCoverFilePath(string bookId, string extension) => Path.Combine(BookStorageService.CoversRoot, $"{bookId}{extension}");

	/// <summary>The same cover file, expressed as a <see cref="ContentRoot"/>-relative path for storage in the catalog.</summary>
	public static string GetCoverRelativePath(string bookId, string extension) => $"Covers/{bookId}{extension}";

	public static string GetAbsolutePath(string relativePath)
	{
		return string.IsNullOrWhiteSpace(relativePath)
			? string.Empty
			: Path.GetFullPath(Path.Combine(BookStorageService.ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
	}

	public static void DeleteBook(string bookId)
	{
		if (string.IsNullOrWhiteSpace(bookId))
		{
			return;
		}

		string epubFilePath = BookStorageService.GetBookFilePath(bookId);
		if (File.Exists(epubFilePath))
		{
			File.Delete(epubFilePath);
		}

		if (Directory.Exists(BookStorageService.CoversRoot))
		{
			foreach (string coverFile in Directory.EnumerateFiles(BookStorageService.CoversRoot, $"{bookId}.*"))
			{
				File.Delete(coverFile);
			}
		}
	}

	public static Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(BookStorageService.ContentRoot);
		Directory.CreateDirectory(BookStorageService.BooksRoot);
		Directory.CreateDirectory(BookStorageService.CoversRoot);
		return Task.CompletedTask;
	}
}