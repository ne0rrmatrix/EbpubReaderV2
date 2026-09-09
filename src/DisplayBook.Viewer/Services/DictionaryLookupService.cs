using Microsoft.Data.Sqlite;
using Microsoft.Maui.Storage;

namespace DisplayBook.Viewer.Services;

/// <summary>
/// Offline word lookup backed by the bundled Webster's 1913 SQLite database (see
/// Resources/Raw/Dictionary/SOURCE.md for provenance/licensing). The database is a
/// read-only MAUI asset, so it's copied out to app data storage once before it can be
/// opened by <see cref="SqliteConnection"/> -- the same approach <see cref="ReaderAssetHost"/>
/// already uses for the reader's own web assets.
/// </summary>
public sealed class DictionaryLookupService : IDictionaryLookupService
{
    private const string AssetPath = "Dictionary/websters1913.db";
    private static readonly SemaphoreSlim ExtractionLock = new(1, 1);
    private static readonly char[] TrimPunctuation = ['.', ',', ';', ':', '!', '?', '"', '“', '”', '(', ')', '[', ']', '{', '}', '—', '–'];

    private static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "Dictionary", "websters1913.db");

    public async Task<DictionaryDefinition?> LookupAsync(string rawSelection, CancellationToken cancellationToken = default)
    {
        var wordKey = NormalizeSelection(rawSelection);
        if (wordKey is null)
        {
            return null;
        }

        await EnsureExtractedAsync(cancellationToken);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var result = await QueryAsync(connection, wordKey, cancellationToken);
        if (result is not null)
        {
            return result;
        }

        foreach (var fallbackKey in BuildFallbackKeys(wordKey))
        {
            result = await QueryAsync(connection, fallbackKey, cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static async Task<DictionaryDefinition?> QueryAsync(SqliteConnection connection, string wordKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Word, Definition FROM Definitions WHERE WordKey = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", wordKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DictionaryDefinition(reader.GetString(0), reader.GetString(1))
            : null;
    }

    /// <summary>Simple singular fallback for a missed exact match ("cats" -&gt; "cat").</summary>
    private static IEnumerable<string> BuildFallbackKeys(string wordKey)
    {
        if (wordKey.EndsWith("es", StringComparison.Ordinal) && wordKey.Length > 3)
        {
            yield return wordKey[..^2];
        }

        if (wordKey.EndsWith('s') && wordKey.Length > 2)
        {
            yield return wordKey[..^1];
        }
    }

    private static string? NormalizeSelection(string rawSelection)
    {
        var firstToken = rawSelection
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(firstToken))
        {
            return null;
        }

        var trimmed = firstToken.Trim(TrimPunctuation);
        if (trimmed.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2];
        }

        trimmed = trimmed.Trim('\'', '‘', '’');
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }

    private static async Task EnsureExtractedAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(DatabasePath))
        {
            return;
        }

        await ExtractionLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(DatabasePath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            var tempPath = DatabasePath + ".tmp";
            await using (var source = await FileSystem.OpenAppPackageFileAsync(AssetPath))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(tempPath, DatabasePath, overwrite: true);
        }
        finally
        {
            ExtractionLock.Release();
        }
    }
}
