namespace DisplayBook.App.Services;

public static class BookStorageService
{
	public static string ContentRoot => Path.Combine(FileSystem.AppDataDirectory, "ReaderContent");

	public static string BooksRoot => Path.Combine(BookStorageService.ContentRoot, "Books");

	public static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "displaybook.db");

	public static string GetBookRoot(string bookId) => Path.Combine(BookStorageService.BooksRoot, bookId);

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

		string bookRoot = BookStorageService.GetBookRoot(bookId);
		if (Directory.Exists(bookRoot))
		{
			Directory.Delete(bookRoot, recursive: true);
		}
	}

	public static Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(BookStorageService.ContentRoot);
		Directory.CreateDirectory(BookStorageService.BooksRoot);
		return Task.CompletedTask;
	}
}