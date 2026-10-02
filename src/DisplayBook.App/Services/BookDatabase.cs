using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using Microsoft.Data.Sqlite;

namespace DisplayBook.App.Services;

public sealed partial class BookDatabase : IBookDatabase, IDisposable
{
	// PublicationRoot now holds a relative path to the book's persisted single .epub file
	// (e.g. "Books/{bookId}.epub") rather than an extracted folder -- kept as-is rather than
	// renamed to avoid churning the positional column/ordinal mapping this file depends on.
	const string bookColumns = """
        Id, Title, Author, Description, Language, Publisher, CoverRelativePath,
                 PublicationRoot, PublicationOpfPath, OriginalFileName, ImportedAt,
                 LastOpenedAt, LocatorResourceHref, LocatorPage, LocatorPageCount, ContentHash,
                 Isbn, Asin, LocatorCharOffset
        """;

	const string textColumnDdl = "TEXT NOT NULL DEFAULT ''";
	const string charOffsetColumnDdl = "INTEGER NOT NULL DEFAULT -1";

	readonly SemaphoreSlim initializationLock = new(1, 1);
	bool initialized;
	bool disposedValue;

	public async Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default)
	{
		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT {bookColumns} FROM Books ORDER BY ImportedAt;";

		List<BookSummary> books = [];
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			BookSummary book = BookDatabase.ReadBook(reader);
			books.Add(book);
		}

		return books;
	}

	public async Task<BookSummary?> GetBookAsync(string bookId, CancellationToken cancellationToken = default)
	{
		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		return await BookDatabase.SelectBookAsync(connection, bookId, cancellationToken);
	}

	public async Task<bool> ContainsContentHashAsync(string contentHash, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(contentHash))
		{
			return false;
		}

		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT EXISTS(SELECT 1 FROM Books WHERE ContentHash = $hash);";
		command.Parameters.AddWithValue("$hash", contentHash);
		object? result = await command.ExecuteScalarAsync(cancellationToken);
		return Convert.ToInt64(result) == 1;
	}

	public async Task AddBookAsync(BookSummary book, string coverRelativePath, CancellationToken cancellationToken = default)
	{
		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
            INSERT INTO Books (
                Id, Title, Author, Description, Language, Publisher, CoverRelativePath,
                PublicationRoot, PublicationOpfPath, OriginalFileName, ImportedAt,
                LastOpenedAt, LocatorResourceHref, LocatorPage, LocatorPageCount, ContentHash,
                Isbn, Asin, LocatorCharOffset)
            VALUES ($id, $title, $author, $description, $language, $publisher, $cover,
                    $root, $opf, $filename, $imported, $opened, $href, $page, $pageCount, $hash,
                    $isbn, $asin, $charOffset);
            """;
		AddBookParameters(command, book, coverRelativePath);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task SaveLocatorAsync(string bookId, string resourceHref, int page, int pageCount, int charOffset = -1, CancellationToken cancellationToken = default)
	{
		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
            UPDATE Books
            SET LastOpenedAt = $opened,
                LocatorResourceHref = $href,
                LocatorPage = $page,
                LocatorPageCount = $pageCount,
                LocatorCharOffset = $charOffset
            WHERE Id = $id;
            """;
		command.Parameters.AddWithValue("$opened", DateTimeOffset.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$href", resourceHref);
		command.Parameters.AddWithValue("$page", page);
		command.Parameters.AddWithValue("$pageCount", pageCount);
		command.Parameters.AddWithValue("$charOffset", charOffset);
		command.Parameters.AddWithValue("$id", bookId);
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	/// <summary>
	/// Overwrites the mutable metadata fields for a book (e.g. from a fetched Amazon match),
	/// snapshotting the pre-update values as a one-level undo (<see cref="UndoMetadataAsync"/>).
	/// A missing <paramref name="newCoverRelativePath"/> leaves the existing cover untouched.
	/// </summary>
	public async Task<BookSummary> UpdateMetadataAsync(
		string bookId,
		BookSummary updated,
		string? newCoverRelativePath,
		CancellationToken cancellationToken = default)
	{
		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		RawBookRow current = await SelectRawRowAsync(connection, bookId, cancellationToken)
			?? throw new InvalidOperationException($"No book found with id '{bookId}'.");

		MetadataSnapshot snapshot = new(
			current.Title, current.Author, current.Description, current.Publisher,
			current.Isbn, current.Asin, current.CoverRelativePath);
		string snapshotJson = JsonSerializer.Serialize(snapshot, MetadataJsonContext.Default.MetadataSnapshot);

		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
            UPDATE Books
            SET Title = $title, Author = $author, Description = $description, Publisher = $publisher,
                Isbn = $isbn, Asin = $asin, CoverRelativePath = $cover, PreviousMetadataJson = $prev
            WHERE Id = $id;
            """;
		command.Parameters.AddWithValue("$title", updated.Title);
		command.Parameters.AddWithValue("$author", updated.Author);
		command.Parameters.AddWithValue("$description", updated.Description);
		command.Parameters.AddWithValue("$publisher", updated.Publisher);
		command.Parameters.AddWithValue("$isbn", updated.Isbn);
		command.Parameters.AddWithValue("$asin", updated.Asin);
		command.Parameters.AddWithValue("$cover", newCoverRelativePath ?? current.CoverRelativePath);
		command.Parameters.AddWithValue("$prev", snapshotJson);
		command.Parameters.AddWithValue("$id", bookId);
		await command.ExecuteNonQueryAsync(cancellationToken);

		return await BookDatabase.SelectBookAsync(connection, bookId, cancellationToken)
			?? throw new InvalidOperationException($"Book '{bookId}' disappeared while updating its metadata.");
	}

	/// <summary>
	/// Restores the metadata snapshotted by the most recent <see cref="UpdateMetadataAsync"/> call
	/// and clears it (one level of undo only). Returns null when nothing is available to undo.
	/// </summary>
	public async Task<BookSummary?> UndoMetadataAsync(string bookId, CancellationToken cancellationToken = default)
	{
		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		string? previousMetadataJson = await SelectPreviousMetadataJsonAsync(connection, bookId, cancellationToken);
		if (string.IsNullOrWhiteSpace(previousMetadataJson))
		{
			return null;
		}

		MetadataSnapshot snapshot = JsonSerializer.Deserialize(previousMetadataJson, MetadataJsonContext.Default.MetadataSnapshot)
			?? throw new InvalidOperationException("The stored metadata snapshot could not be read.");

		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
            UPDATE Books
            SET Title = $title, Author = $author, Description = $description, Publisher = $publisher,
                Isbn = $isbn, Asin = $asin, CoverRelativePath = $cover, PreviousMetadataJson = ''
            WHERE Id = $id;
            """;
		command.Parameters.AddWithValue("$title", snapshot.Title);
		command.Parameters.AddWithValue("$author", snapshot.Author);
		command.Parameters.AddWithValue("$description", snapshot.Description);
		command.Parameters.AddWithValue("$publisher", snapshot.Publisher);
		command.Parameters.AddWithValue("$isbn", snapshot.Isbn);
		command.Parameters.AddWithValue("$asin", snapshot.Asin);
		command.Parameters.AddWithValue("$cover", snapshot.CoverRelativePath);
		command.Parameters.AddWithValue("$id", bookId);
		await command.ExecuteNonQueryAsync(cancellationToken);

		return await BookDatabase.SelectBookAsync(connection, bookId, cancellationToken);
	}

	public async Task<bool> HasPreviousMetadataAsync(string bookId, CancellationToken cancellationToken = default)
	{
		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		string? json = await SelectPreviousMetadataJsonAsync(connection, bookId, cancellationToken);
		return !string.IsNullOrWhiteSpace(json);
	}

	public async Task DeleteBooksAsync(IReadOnlyCollection<string> bookIds, CancellationToken cancellationToken = default)
	{
		string[] ids = [.. bookIds
			.Where(bookId => !string.IsNullOrWhiteSpace(bookId))
			.Distinct(StringComparer.Ordinal)];
		if (ids.Length == 0)
		{
			return;
		}

		await InitializeAsync(cancellationToken);
		await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
		await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
		await using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM Books WHERE Id = $id;";

		foreach (string? bookId in ids)
		{
			cancellationToken.ThrowIfCancellationRequested();
			command.Parameters.Clear();
			command.Parameters.AddWithValue("$id", bookId);
			await command.ExecuteNonQueryAsync(cancellationToken);
		}

		await transaction.CommitAsync(cancellationToken);

		List<Exception> cleanupFailures = [];
		foreach (string? bookId in ids)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				BookStorageService.DeleteBook(bookId);
			}
			catch (Exception exception)
			{
				cleanupFailures.Add(new IOException($"Stored content for book '{bookId}' could not be removed.", exception));
			}
		}

		if (cleanupFailures.Count > 0)
		{
			throw new AggregateException("Some stored book content could not be removed.", cleanupFailures);
		}
	}

	async Task InitializeAsync(CancellationToken cancellationToken)
	{
		if (initialized)
		{
			return;
		}

		await initializationLock.WaitAsync(cancellationToken);
		try
		{
			if (initialized)
			{
				return;
			}

			await BookStorageService.InitializeAsync(cancellationToken);
			await using SqliteConnection connection = await BookDatabase.OpenConnectionAsync(cancellationToken);
			await using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
                CREATE TABLE IF NOT EXISTS Books (
                    Id TEXT PRIMARY KEY NOT NULL,
                    Title TEXT NOT NULL,
                    Author TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Language TEXT NOT NULL,
                    Publisher TEXT NOT NULL,
                    CoverRelativePath TEXT NOT NULL,
                    PublicationRoot TEXT NOT NULL,
                    PublicationOpfPath TEXT NOT NULL,
                    OriginalFileName TEXT NOT NULL,
                    ImportedAt TEXT NOT NULL,
                    LastOpenedAt TEXT NULL,
                    LocatorResourceHref TEXT NOT NULL,
                    LocatorPage INTEGER NOT NULL,
                    LocatorPageCount INTEGER NOT NULL,
                    ContentHash TEXT NOT NULL DEFAULT ''
                );
                """;
			await command.ExecuteNonQueryAsync(cancellationToken);
			await EnsureColumnAsync(connection, "ContentHash", textColumnDdl, cancellationToken);
			await EnsureColumnAsync(connection, "Isbn", textColumnDdl, cancellationToken);
			await EnsureColumnAsync(connection, "Asin", textColumnDdl, cancellationToken);
			await EnsureColumnAsync(connection, "PreviousMetadataJson", textColumnDdl, cancellationToken);
			await EnsureColumnAsync(connection, "LocatorCharOffset", charOffsetColumnDdl, cancellationToken);
			await using SqliteCommand indexCommand = connection.CreateCommand();
			indexCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Books_ContentHash ON Books(ContentHash) WHERE ContentHash <> '';";
			await indexCommand.ExecuteNonQueryAsync(cancellationToken);
			await MigrateLegacyExtractedBooksAsync(connection, cancellationToken);
			initialized = true;
		}
		finally
		{
			initializationLock.Release();
		}
	}

	/// <summary>
	/// One-time recovery for libraries imported before this app switched from "extract every
	/// file to disk" to "persist the original .epub and parse it into memory when reading": a
	/// legacy row's PublicationRoot still points at a folder rather than a file. Since all of the
	/// book's original files are still sitting in that folder, it's zipped back into a synthetic
	/// but valid .epub at the new location rather than forcing a re-import. The old folder is
	/// left in place (not deleted) so a failure here never loses data.
	/// </summary>
	static async Task MigrateLegacyExtractedBooksAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		List<(string Id, string PublicationRoot)> rows = [];
		await using (SqliteCommand selectCommand = connection.CreateCommand())
		{
			selectCommand.CommandText = "SELECT Id, PublicationRoot FROM Books;";
			await using SqliteDataReader reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				rows.Add((reader.GetString(0), reader.GetString(1)));
			}
		}

		foreach ((string id, string publicationRoot) in rows)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string legacyFolder = BookStorageService.GetAbsolutePath(publicationRoot);
			string newEpubPath = BookStorageService.GetBookFilePath(id);
			if (!Directory.Exists(legacyFolder) || File.Exists(newEpubPath))
			{
				continue;
			}

			try
			{
				Directory.CreateDirectory(BookStorageService.BooksRoot);
				CreateArchiveFromLegacyFolder(legacyFolder, newEpubPath, cancellationToken);

				await using SqliteCommand updateCommand = connection.CreateCommand();
				updateCommand.CommandText = "UPDATE Books SET PublicationRoot = $root WHERE Id = $id;";
				updateCommand.Parameters.AddWithValue("$root", $"Books/{id}.epub");
				updateCommand.Parameters.AddWithValue("$id", id);
				await updateCommand.ExecuteNonQueryAsync(cancellationToken);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Leave the legacy folder and row untouched -- the book just won't open until a
				// later launch (or a disk-space/permissions fix) lets this migration succeed.
				DeleteFileIfExists(newEpubPath);
			}
		}
	}

	static void CreateArchiveFromLegacyFolder(string sourceRoot, string destinationEpubPath, CancellationToken cancellationToken)
	{
		using FileStream destination = File.Create(destinationEpubPath);
		using ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: false);
		foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			cancellationToken.ThrowIfCancellationRequested();
			string relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
			archive.CreateEntryFromFile(file, relative);
		}
	}

	static void DeleteFileIfExists(string path)
	{
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	static async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
	{
		SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = BookStorageService.DatabasePath,
			Mode = SqliteOpenMode.ReadWriteCreate
		}.ToString());
		await connection.OpenAsync(cancellationToken);
		return connection;
	}

	static async Task<BookSummary?> SelectBookAsync(SqliteConnection connection, string bookId, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT {bookColumns} FROM Books WHERE Id = $id;";
		command.Parameters.AddWithValue("$id", bookId);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		return await reader.ReadAsync(cancellationToken) ? BookDatabase.ReadBook(reader) : null;
	}

	static async Task<RawBookRow?> SelectRawRowAsync(SqliteConnection connection, string bookId, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT Title, Author, Description, Publisher, Isbn, Asin, CoverRelativePath FROM Books WHERE Id = $id;";
		command.Parameters.AddWithValue("$id", bookId);
		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		return !await reader.ReadAsync(cancellationToken)
			? null
			: new RawBookRow(
			reader.GetString(0),
			reader.GetString(1),
			reader.GetString(2),
			reader.GetString(3),
			reader.GetString(4),
			reader.GetString(5),
			reader.GetString(6));
	}

	static async Task<string?> SelectPreviousMetadataJsonAsync(SqliteConnection connection, string bookId, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT PreviousMetadataJson FROM Books WHERE Id = $id;";
		command.Parameters.AddWithValue("$id", bookId);
		object? result = await command.ExecuteScalarAsync(cancellationToken);
		return result as string;
	}

	static BookSummary ReadBook(SqliteDataReader reader)
	{
		string coverRelativePath = reader.GetString(6);
		return new BookSummary(
			reader.GetString(0),
			reader.GetString(1),
			reader.GetString(2),
			reader.GetString(3),
			reader.GetString(4),
			reader.GetString(5),
			BookStorageService.GetAbsolutePath(coverRelativePath),
			reader.GetString(7),
			reader.GetString(8),
			reader.GetString(9),
			DateTimeOffset.Parse(reader.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
			reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
			reader.GetString(12),
			reader.GetInt32(13),
			reader.GetInt32(14),
			reader.GetString(15),
			reader.GetString(16),
			reader.GetString(17),
			reader.GetInt32(18));
	}

	static async Task EnsureColumnAsync(SqliteConnection connection, string column, string ddlType, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "PRAGMA table_info(Books);";
		bool hasColumn = false;
		await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
		{
			while (await reader.ReadAsync(cancellationToken))
			{
				if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
				{
					hasColumn = true;
					break;
				}
			}
		}

		if (hasColumn)
		{
			return;
		}

		await using SqliteCommand alterCommand = connection.CreateCommand();
		alterCommand.CommandText = $"ALTER TABLE Books ADD COLUMN {column} {ddlType};";
		await alterCommand.ExecuteNonQueryAsync(cancellationToken);
	}

	static void AddBookParameters(SqliteCommand command, BookSummary book, string coverRelativePath)
	{
		command.Parameters.AddWithValue("$id", book.Id);
		command.Parameters.AddWithValue("$title", book.Title);
		command.Parameters.AddWithValue("$author", book.Author);
		command.Parameters.AddWithValue("$description", book.Description);
		command.Parameters.AddWithValue("$language", book.Language);
		command.Parameters.AddWithValue("$publisher", book.Publisher);
		command.Parameters.AddWithValue("$cover", coverRelativePath);
		command.Parameters.AddWithValue("$root", book.EpubRelativePath);
		command.Parameters.AddWithValue("$opf", book.PublicationOpfPath);
		command.Parameters.AddWithValue("$filename", book.OriginalFileName);
		command.Parameters.AddWithValue("$imported", book.ImportedAt.ToString("O"));
		command.Parameters.AddWithValue("$opened", book.LastOpenedAt is null ? DBNull.Value : book.LastOpenedAt.Value.ToString("O"));
		command.Parameters.AddWithValue("$href", book.LocatorResourceHref);
		command.Parameters.AddWithValue("$page", book.LocatorPage);
		command.Parameters.AddWithValue("$pageCount", book.LocatorPageCount);
		command.Parameters.AddWithValue("$hash", book.ContentHash);
		command.Parameters.AddWithValue("$isbn", book.Isbn);
		command.Parameters.AddWithValue("$asin", book.Asin);
		command.Parameters.AddWithValue("$charOffset", book.LocatorCharOffset);
	}

	sealed record RawBookRow(
		string Title,
		string Author,
		string Description,
		string Publisher,
		string Isbn,
		string Asin,
		string CoverRelativePath);

	sealed record MetadataSnapshot(
		string Title,
		string Author,
		string Description,
		string Publisher,
		string Isbn,
		string Asin,
		string CoverRelativePath);

	[JsonSerializable(typeof(MetadataSnapshot))]
	partial class MetadataJsonContext : JsonSerializerContext
	{
	}

	void Dispose(bool disposing)
	{
		if (!disposedValue)
		{
			if (disposing)
			{
				initializationLock.Dispose();
			}

			disposedValue = true;
		}
	}

	public void Dispose()
	{
		// Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}