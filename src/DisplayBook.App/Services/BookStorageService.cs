namespace DisplayBook.App.Services;

public sealed class BookStorageService
{
	public string ContentRoot => Path.Combine(FileSystem.AppDataDirectory, "ReaderContent");

	public string BooksRoot => Path.Combine(ContentRoot, "Books");

	public string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "displaybook.db");

	public string GetBookRoot(string bookId) => Path.Combine(BooksRoot, bookId);

	public string GetAbsolutePath(string relativePath)
	{
		return string.IsNullOrWhiteSpace(relativePath)
			? string.Empty
			: Path.GetFullPath(Path.Combine(ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
	}

	public void DeleteBook(string bookId)
	{
		if (string.IsNullOrWhiteSpace(bookId))
		{
			return;
		}

		string bookRoot = GetBookRoot(bookId);
		if (Directory.Exists(bookRoot))
		{
			Directory.Delete(bookRoot, recursive: true);
		}
	}

	public Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Directory.CreateDirectory(ContentRoot);
		Directory.CreateDirectory(BooksRoot);
		return Task.CompletedTask;
	}
}