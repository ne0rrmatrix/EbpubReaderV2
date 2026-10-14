using System.IO.Compression;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.DeviceTests.Support;
using Microsoft.Data.Sqlite;

namespace DisplayBook.DeviceTests.Tests;

[Collection(AppDataCollection.Name)]
public sealed partial class BookDatabaseTests : IDisposable
{
	static readonly DateTimeOffset importedAt = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

	readonly BookDatabase database = new();

	public BookDatabaseTests() => AppDataSandbox.Reset();

	public void Dispose() => database.Dispose();

	static BookSummary MakeBook(string id, string contentHash = "", DateTimeOffset? importedAtOverride = null) => new(
		Id: id,
		Title: $"Title {id}",
		Author: "Author",
		Description: "Description",
		Language: "en",
		Publisher: "Publisher",
		CoverPath: string.Empty,
		EpubRelativePath: $"Books/{id}.epub",
		PublicationOpfPath: "OEBPS/content.opf",
		OriginalFileName: $"{id}.epub",
		ImportedAt: importedAtOverride ?? importedAt,
		LastOpenedAt: null,
		LocatorResourceHref: "Text/ch1.xhtml",
		LocatorPage: 1,
		LocatorPageCount: 10,
		ContentHash: contentHash,
		Isbn: "9780000000001",
		Asin: "B000000001",
		LocatorCharOffset: 42);

	[Fact]
	public async Task GetBooks_NewLibrary_IsEmptyAndCreatesTheDatabase()
	{
		Assert.Empty(await database.GetBooksAsync());
		Assert.True(File.Exists(BookStorageService.DatabasePath));
	}

	[Fact]
	public async Task AddBook_RoundTripsEveryColumn()
	{
		BookSummary book = MakeBook("a", contentHash: "hash-a");

		await database.AddBookAsync(book, "Covers/a.jpg");
		BookSummary? stored = await database.GetBookAsync("a");

		// CoverPath is stored relative and read back absolute; everything else is as written.
		Assert.Equal(book with { CoverPath = BookStorageService.GetAbsolutePath("Covers/a.jpg") }, stored);
	}

	[Fact]
	public async Task GetBook_UnknownId_ReturnsNull()
	{
		Assert.Null(await database.GetBookAsync("missing"));
	}

	[Fact]
	public async Task GetBooks_OrdersByImportTime()
	{
		await database.AddBookAsync(MakeBook("late", importedAtOverride: importedAt.AddDays(2)), string.Empty);
		await database.AddBookAsync(MakeBook("early", importedAtOverride: importedAt), string.Empty);
		await database.AddBookAsync(MakeBook("middle", importedAtOverride: importedAt.AddDays(1)), string.Empty);

		IReadOnlyList<BookSummary> books = await database.GetBooksAsync();

		Assert.Equal(["early", "middle", "late"], books.Select(book => book.Id));
	}

	[Fact]
	public async Task ContainsContentHash_MatchesOnlyStoredHashes()
	{
		await database.AddBookAsync(MakeBook("a", contentHash: "hash-a"), string.Empty);

		Assert.True(await database.ContainsContentHashAsync("hash-a"));
		Assert.False(await database.ContainsContentHashAsync("hash-b"));
		Assert.False(await database.ContainsContentHashAsync("  "));
	}

	[Fact]
	public async Task AddBook_DuplicateContentHash_IsRejected()
	{
		await database.AddBookAsync(MakeBook("a", contentHash: "same"), string.Empty);

		await Assert.ThrowsAsync<SqliteException>(() => database.AddBookAsync(MakeBook("b", contentHash: "same"), string.Empty));
	}

	[Fact]
	public async Task AddBook_EmptyContentHash_IsAllowedMoreThanOnce()
	{
		await database.AddBookAsync(MakeBook("a"), string.Empty);
		await database.AddBookAsync(MakeBook("b"), string.Empty);

		Assert.Equal(2, (await database.GetBooksAsync()).Count);
	}

	[Fact]
	public async Task SaveLocator_UpdatesPositionAndLastOpened()
	{
		await database.AddBookAsync(MakeBook("a"), string.Empty);
		DateTimeOffset before = DateTimeOffset.UtcNow;

		await database.SaveLocatorAsync("a", "Text/ch7.xhtml", 3, 12, charOffset: 900);
		BookSummary stored = (await database.GetBookAsync("a"))!;

		Assert.Equal(("Text/ch7.xhtml", 3, 12, 900), (stored.LocatorResourceHref, stored.LocatorPage, stored.LocatorPageCount, stored.LocatorCharOffset));
		Assert.NotNull(stored.LastOpenedAt);
		Assert.True(stored.LastOpenedAt >= before.AddSeconds(-1));
	}

	[Fact]
	public async Task UpdateMetadata_ThenUndo_RestoresThePreviousValues()
	{
		BookSummary original = MakeBook("a");
		await database.AddBookAsync(original, "Covers/a.jpg");

		BookSummary updated = await database.UpdateMetadataAsync(
			"a",
			original with { Title = "New title", Author = "New author", Isbn = "9781111111111" },
			"Covers/a-new.png");

		Assert.Equal(("New title", "New author", "9781111111111"), (updated.Title, updated.Author, updated.Isbn));
		Assert.Equal(BookStorageService.GetAbsolutePath("Covers/a-new.png"), updated.CoverPath);
		Assert.True(await database.HasPreviousMetadataAsync("a"));

		BookSummary? restored = await database.UndoMetadataAsync("a");

		Assert.NotNull(restored);
		Assert.Equal((original.Title, original.Author, original.Isbn), (restored.Title, restored.Author, restored.Isbn));
		Assert.Equal(BookStorageService.GetAbsolutePath("Covers/a.jpg"), restored.CoverPath);
		Assert.False(await database.HasPreviousMetadataAsync("a"));
	}

	[Fact]
	public async Task UndoMetadata_IsOneLevelOnly()
	{
		BookSummary original = MakeBook("a");
		await database.AddBookAsync(original, string.Empty);
		await database.UpdateMetadataAsync("a", original with { Title = "Once" }, null);

		Assert.NotNull(await database.UndoMetadataAsync("a"));
		Assert.Null(await database.UndoMetadataAsync("a"));
	}

	[Fact]
	public async Task UpdateMetadata_NoNewCover_KeepsTheExistingCover()
	{
		BookSummary original = MakeBook("a");
		await database.AddBookAsync(original, "Covers/a.jpg");

		BookSummary updated = await database.UpdateMetadataAsync("a", original with { Title = "Renamed" }, newCoverRelativePath: null);

		Assert.Equal(BookStorageService.GetAbsolutePath("Covers/a.jpg"), updated.CoverPath);
	}

	[Fact]
	public async Task UpdateMetadata_UnknownBook_Throws()
	{
		await Assert.ThrowsAsync<InvalidOperationException>(() => database.UpdateMetadataAsync("missing", MakeBook("missing"), null));
	}

	[Fact]
	public async Task DeleteBooks_RemovesRowsAndStoredFiles()
	{
		await database.AddBookAsync(MakeBook("a", contentHash: "hash-a"), "Covers/a.jpg");
		await database.AddBookAsync(MakeBook("b", contentHash: "hash-b"), "Covers/b.jpg");
		string epubA = BookStorageService.GetBookFilePath("a");
		string coverA = BookStorageService.GetCoverFilePath("a", ".jpg");
		await File.WriteAllTextAsync(epubA, "epub");
		await File.WriteAllTextAsync(coverA, "cover");

		await database.DeleteBooksAsync(["a", "a", " "]);

		Assert.Equal(["b"], (await database.GetBooksAsync()).Select(book => book.Id));
		Assert.False(File.Exists(epubA));
		Assert.False(File.Exists(coverA));
		Assert.False(await database.ContainsContentHashAsync("hash-a"));
	}

	[Fact]
	public async Task OpeningAnOlderDatabase_AddsTheMissingColumnsWithDefaults()
	{
		// The schema as it was before ContentHash/Isbn/Asin/PreviousMetadataJson/LocatorCharOffset.
		await ExecuteAsync("""
			CREATE TABLE Books (
				Id TEXT PRIMARY KEY NOT NULL, Title TEXT NOT NULL, Author TEXT NOT NULL,
				Description TEXT NOT NULL, Language TEXT NOT NULL, Publisher TEXT NOT NULL,
				CoverRelativePath TEXT NOT NULL, PublicationRoot TEXT NOT NULL,
				PublicationOpfPath TEXT NOT NULL, OriginalFileName TEXT NOT NULL,
				ImportedAt TEXT NOT NULL, LastOpenedAt TEXT NULL, LocatorResourceHref TEXT NOT NULL,
				LocatorPage INTEGER NOT NULL, LocatorPageCount INTEGER NOT NULL);
			INSERT INTO Books VALUES ('old', 'Old', 'Author', '', 'en', '', '', 'Books/old.epub',
				'content.opf', 'old.epub', '2020-01-01T00:00:00.0000000+00:00', NULL, 'ch1.xhtml', 2, 9);
			""");

		BookSummary stored = Assert.Single(await database.GetBooksAsync());

		Assert.Equal(("old", 2, 9), (stored.Id, stored.LocatorPage, stored.LocatorPageCount));
		Assert.Equal((string.Empty, string.Empty, string.Empty, -1), (stored.ContentHash, stored.Isbn, stored.Asin, stored.LocatorCharOffset));
	}

	[Fact]
	public async Task OpeningALegacyExtractedBook_ZipsItIntoAnEpubFile()
	{
		string legacyFolder = Path.Combine(BookStorageService.ContentRoot, "Legacy", "old");
		Directory.CreateDirectory(Path.Combine(legacyFolder, "OEBPS"));
		await File.WriteAllTextAsync(Path.Combine(legacyFolder, "mimetype"), "application/epub+zip");
		await File.WriteAllTextAsync(Path.Combine(legacyFolder, "OEBPS", "ch1.xhtml"), "<html/>");
		await ExecuteAsync("""
			CREATE TABLE Books (
				Id TEXT PRIMARY KEY NOT NULL, Title TEXT NOT NULL, Author TEXT NOT NULL,
				Description TEXT NOT NULL, Language TEXT NOT NULL, Publisher TEXT NOT NULL,
				CoverRelativePath TEXT NOT NULL, PublicationRoot TEXT NOT NULL,
				PublicationOpfPath TEXT NOT NULL, OriginalFileName TEXT NOT NULL,
				ImportedAt TEXT NOT NULL, LastOpenedAt TEXT NULL, LocatorResourceHref TEXT NOT NULL,
				LocatorPage INTEGER NOT NULL, LocatorPageCount INTEGER NOT NULL);
			INSERT INTO Books VALUES ('old', 'Old', 'Author', '', 'en', '', '', 'Legacy/old',
				'content.opf', 'old.epub', '2020-01-01T00:00:00.0000000+00:00', NULL, 'ch1.xhtml', 0, 1);
			""");

		BookSummary stored = Assert.Single(await database.GetBooksAsync());

		Assert.Equal("Books/old.epub", stored.EpubRelativePath);
		using ZipArchive archive = ZipFile.OpenRead(BookStorageService.GetBookFilePath("old"));
		Assert.Equal(["OEBPS/ch1.xhtml", "mimetype"], archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
		Assert.True(Directory.Exists(legacyFolder), "The legacy folder is kept so a failed migration never loses data.");
	}

	static async Task ExecuteAsync(string sql)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(BookStorageService.DatabasePath)!);
		await using SqliteConnection connection = new($"Data Source={BookStorageService.DatabasePath}");
		await connection.OpenAsync();
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		await command.ExecuteNonQueryAsync();
	}
}
