using DisplayBook.App.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace DisplayBook.App.Services;

public sealed class BookDatabase(BookStorageService storage) : IBookDatabase
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public async Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Author, Description, Language, Publisher, CoverRelativePath,
                     PublicationRoot, PublicationOpfPath, OriginalFileName, ImportedAt,
                     LastOpenedAt, LocatorResourceHref, LocatorPage, LocatorPageCount, ContentHash
            FROM Books
            ORDER BY ImportedAt;
            """;

        var books = new List<BookSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var book = ReadBook(reader);
            books.Add(book);
        }

        return books;
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
                LastOpenedAt, LocatorResourceHref, LocatorPage, LocatorPageCount, ContentHash)
            VALUES ($id, $title, $author, $description, $language, $publisher, $cover,
                    $root, $opf, $filename, $imported, $opened, $href, $page, $pageCount, $hash);
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
            await EnsureContentHashColumnAsync(connection, cancellationToken);
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
            reader.GetString(15));
    }

    private static async Task EnsureContentHashColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Books);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var hasContentHash = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "ContentHash", StringComparison.OrdinalIgnoreCase))
            {
                hasContentHash = true;
                break;
            }
        }

        if (hasContentHash)
        {
            return;
        }

        await reader.DisposeAsync();
        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = "ALTER TABLE Books ADD COLUMN ContentHash TEXT NOT NULL DEFAULT '';";
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
    }
}