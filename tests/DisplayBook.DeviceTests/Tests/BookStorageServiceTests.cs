using DisplayBook.App.Services;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests;

public class BookStorageServiceTests : AppDataTest
{
	[Fact]
	public void Paths_AreUnderTheAppDataDirectory()
	{
		string appData = FileSystem.AppDataDirectory;

		Assert.Equal(Path.Combine(appData, "ReaderContent"), BookStorageService.ContentRoot);
		Assert.Equal(Path.Combine(appData, "ReaderContent", "Books"), BookStorageService.BooksRoot);
		Assert.Equal(Path.Combine(appData, "ReaderContent", "Covers"), BookStorageService.CoversRoot);
		Assert.Equal(Path.Combine(appData, "displaybook.db"), BookStorageService.DatabasePath);
	}

	[Fact]
	public void GetBookFilePath_IsTheBookIdEpubUnderBooksRoot()
	{
		Assert.Equal(Path.Combine(BookStorageService.BooksRoot, "abc.epub"), BookStorageService.GetBookFilePath("abc"));
	}

	[Fact]
	public void CoverPaths_AbsoluteAndRelativeResolveToTheSameFile()
	{
		string relative = BookStorageService.GetCoverRelativePath("abc", ".png");

		Assert.Equal("Covers/abc.png", relative);
		Assert.Equal(
			Path.GetFullPath(BookStorageService.GetCoverFilePath("abc", ".png")),
			BookStorageService.GetAbsolutePath(relative));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void GetAbsolutePath_Blank_ReturnsEmpty(string relativePath)
	{
		Assert.Equal(string.Empty, BookStorageService.GetAbsolutePath(relativePath));
	}

	[Fact]
	public async Task InitializeAsync_CreatesTheContentFolders()
	{
		await BookStorageService.InitializeAsync();

		Assert.True(Directory.Exists(BookStorageService.ContentRoot));
		Assert.True(Directory.Exists(BookStorageService.BooksRoot));
		Assert.True(Directory.Exists(BookStorageService.CoversRoot));
	}

	[Fact]
	public async Task InitializeAsync_CancelledToken_ThrowsWithoutCreatingFolders()
	{
		// Creating the database schema creates the folders too, so start without it.
		await AppDataSandbox.ResetAsync(primeDatabase: false);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BookStorageService.InitializeAsync(new CancellationToken(canceled: true)));

		Assert.False(Directory.Exists(BookStorageService.ContentRoot));
	}

	[Fact]
	public async Task DeleteBook_RemovesItsEpubAndEveryCoverButLeavesOtherBooks()
	{
		await BookStorageService.InitializeAsync();
		string[] doomed =
		[
			BookStorageService.GetBookFilePath("doomed"),
			BookStorageService.GetCoverFilePath("doomed", ".jpg"),
			BookStorageService.GetCoverFilePath("doomed", ".png"),
		];
		string[] kept =
		[
			BookStorageService.GetBookFilePath("kept"),
			BookStorageService.GetCoverFilePath("kept", ".jpg"),
		];
		foreach (string path in doomed.Concat(kept))
		{
			await File.WriteAllTextAsync(path, "x");
		}

		BookStorageService.DeleteBook("doomed");

		Assert.All(doomed, path => Assert.False(File.Exists(path), path));
		Assert.All(kept, path => Assert.True(File.Exists(path), path));
	}

	[Fact]
	public void DeleteBook_NothingStored_DoesNotThrow()
	{
		Assert.Null(Record.Exception(() => BookStorageService.DeleteBook("never-imported")));
		Assert.Null(Record.Exception(() => BookStorageService.DeleteBook("  ")));
	}
}
