using DisplayBook.App.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace DisplayBook.App.Services;

public sealed class BookDatabase(BookStorageService storage) : IBookDatabase
{
    private const string BookColumns = """
        Id, Title, Author, Description, Language, Publisher, CoverRelativePath,
                 PublicationRoot, PublicationOpfPath, OriginalFileName, ImportedAt,
                 LastOpenedAt, LocatorResourceHref, LocatorPage, LocatorPageCount, ContentHash,
                 Isbn, Asin
        """;

    private const string TextColumnDdl = "TEXT NOT NULL DEFAULT ''";

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public async Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BookColumns} FROM Books ORDER BY ImportedAt;";

        var books = new List<BookSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var book = ReadBook(reader);
            books.Add(book);
        }

        return books;
    }

    public async Task<BookSummary?> GetBookAsync(string bookId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await SelectBookAsync(connection, bookId, cancellationToken);
    }

    public async Task<bool> ContainsContentHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return false;
        }

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Books WHERE ContentHash = $hash);";
        command.Parameters.AddWithValue("$hash", contentHash);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result) == 1;
    }

    public async Task AddBookAsync(BookSummary book, string coverRelativePath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Books (
                Id, Title, Author, Description, Language, Publisher, CoverRelativePath,
                PublicationRoot, PublicationOpfPath, OriginalFileName, ImportedAt,
                LastOpenedAt, LocatorResourceHref, LocatorPage, LocatorPageCount, ContentHash,
                Isbn, Asin)
            VALUES ($id, $title, $author, $description, $language, $publisher, $cover,
                    $root, $opf, $filename, $imported, $opened, $href, $page, $pageCount, $hash,
                    $isbn, $asin);
            """;
        AddBookParameters(command, book, coverRelativePath);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveLocatorAsync(string bookId, string resourceHref, int page, int pageCount, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Books
            SET LastOpenedAt = $opened,
                LocatorResourceHref = $href,
                LocatorPage = $page,
                LocatorPageCount = $pageCount
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$opened", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$href", resourceHref);
        command.Parameters.AddWithValue("$page", page);
        command.Parameters.AddWithValue("$pageCount", pageCount);
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var current = await SelectRawRowAsync(connection, bookId, cancellationToken)
            ?? throw new InvalidOperationException($"No book found with id '{bookId}'.");

        var snapshot = new MetadataSnapshot(
            current.Title, current.Author, current.Description, current.Publisher,
            current.Isbn, current.Asin, current.CoverRelativePath);
        var snapshotJson = JsonSerializer.Serialize(snapshot);

        await using var command = connection.CreateCommand();
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

        return await SelectBookAsync(connection, bookId, cancellationToken)
            ?? throw new InvalidOperationException($"Book '{bookId}' disappeared while updating its metadata.");
    }

    /// <summary>
    /// Restores the metadata snapshotted by the most recent <see cref="UpdateMetadataAsync"/> call
    /// and clears it (one level of undo only). Returns null when nothing is available to undo.
    /// </summary>
    public async Task<BookSummary?> UndoMetadataAsync(string bookId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var previousMetadataJson = await SelectPreviousMetadataJsonAsync(connection, bookId, cancellationToken);
        if (string.IsNullOrWhiteSpace(previousMetadataJson))
        {
            return null;
        }

        var snapshot = JsonSerializer.Deserialize<MetadataSnapshot>(previousMetadataJson)
            ?? throw new InvalidOperationException("The stored metadata snapshot could not be read.");

        await using var command = connection.CreateCommand();
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

        return await SelectBookAsync(connection, bookId, cancellationToken);
    }

    public async Task<bool> HasPreviousMetadataAsync(string bookId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var json = await SelectPreviousMetadataJsonAsync(connection, bookId, cancellationToken);
        return !string.IsNullOrWhiteSpace(json);
    }

    public async Task DeleteBooksAsync(IReadOnlyCollection<string> bookIds, CancellationToken cancellationToken = default)
    {
        var ids = bookIds
            .Where(bookId => !string.IsNullOrWhiteSpace(bookId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM Books WHERE Id = $id;";

        foreach (var bookId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", bookId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var cleanupFailures = new List<Exception>();
        foreach (var bookId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                storage.DeleteBook(bookId);
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

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await storage.InitializeAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
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
            await EnsureColumnAsync(connection, "ContentHash", TextColumnDdl, cancellationToken);
            await EnsureColumnAsync(connection, "Isbn", TextColumnDdl, cancellationToken);
            await EnsureColumnAsync(connection, "Asin", TextColumnDdl, cancellationToken);
            await EnsureColumnAsync(connection, "PreviousMetadataJson", TextColumnDdl, cancellationToken);
            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Books_ContentHash ON Books(ContentHash) WHERE ContentHash <> '';";
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = storage.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<BookSummary?> SelectBookAsync(SqliteConnection connection, string bookId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BookColumns} FROM Books WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBook(reader) : null;
    }

    private static async Task<RawBookRow?> SelectRawRowAsync(SqliteConnection connection, string bookId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Title, Author, Description, Publisher, Isbn, Asin, CoverRelativePath FROM Books WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RawBookRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6));
    }

    private static async Task<string?> SelectPreviousMetadataJsonAsync(SqliteConnection connection, string bookId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PreviousMetadataJson FROM Books WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", bookId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    private BookSummary ReadBook(SqliteDataReader reader)
    {
        var coverRelativePath = reader.GetString(6);
        return new BookSummary(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            storage.GetAbsolutePath(coverRelativePath),
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
            reader.GetString(17));
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string column, string ddlType, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Books);";
        var hasColumn = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
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

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE Books ADD COLUMN {column} {ddlType};";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddBookParameters(SqliteCommand command, BookSummary book, string coverRelativePath)
    {
        command.Parameters.AddWithValue("$id", book.Id);
        command.Parameters.AddWithValue("$title", book.Title);
        command.Parameters.AddWithValue("$author", book.Author);
        command.Parameters.AddWithValue("$description", book.Description);
        command.Parameters.AddWithValue("$language", book.Language);
        command.Parameters.AddWithValue("$publisher", book.Publisher);
        command.Parameters.AddWithValue("$cover", coverRelativePath);
        command.Parameters.AddWithValue("$root", book.PublicationRoot);
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
    }

    private sealed record RawBookRow(
        string Title,
        string Author,
        string Description,
        string Publisher,
        string Isbn,
        string Asin,
        string CoverRelativePath);

    private sealed record MetadataSnapshot(
        string Title,
        string Author,
        string Description,
        string Publisher,
        string Isbn,
        string Asin,
        string CoverRelativePath);
}
