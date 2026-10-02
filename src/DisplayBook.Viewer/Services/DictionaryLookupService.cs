using Microsoft.Data.Sqlite;

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
	const string assetPath = "Dictionary/websters1913.db";
	static readonly SemaphoreSlim extractionLock = new(1, 1);
	static readonly char[] trimPunctuation = ['.', ',', ';', ':', '!', '?', '"', '“', '”', '(', ')', '[', ']', '{', '}', '—', '–'];

	static string DatabasePath => Path.Combine(FileSystem.AppDataDirectory, "Dictionary", "websters1913.db");

	public async Task<DictionaryDefinition?> LookupAsync(string rawSelection, CancellationToken cancellationToken = default)
	{
		string? wordKey = NormalizeSelection(rawSelection);
		if (wordKey is null)
		{
			return null;
		}

		await EnsureExtractedAsync(cancellationToken);

		string connectionString = new SqliteConnectionStringBuilder
		{
			DataSource = DatabasePath,
			Mode = SqliteOpenMode.ReadOnly
		}.ToString();

		await using SqliteConnection connection = new(connectionString);
		await connection.OpenAsync(cancellationToken);

		DictionaryDefinition? result = await QueryAsync(connection, wordKey, cancellationToken);
		if (result is not null)
		{
			return result;
		}

		foreach (string fallbackKey in BuildFallbackKeys(wordKey))
		{
			result = await QueryAsync(connection, fallbackKey, cancellationToken);
			if (result is not null)
			{
				return result;
			}
		}

		return null;
	}

	static async Task<DictionaryDefinition?> QueryAsync(SqliteConnection connection, string wordKey, CancellationToken cancellationToken)
	{
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT Word, Definition FROM Definitions WHERE WordKey = $key LIMIT 1;";
		command.Parameters.AddWithValue("$key", wordKey);

		await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
		return await reader.ReadAsync(cancellationToken)
			? new DictionaryDefinition(reader.GetString(0), reader.GetString(1))
			: null;
	}

	/// <summary>Simple singular fallback for a missed exact match ("cats" -&gt; "cat").</summary>
	static IEnumerable<string> BuildFallbackKeys(string wordKey)
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

	static string? NormalizeSelection(string rawSelection)
	{
		string? firstToken = rawSelection
			.Trim()
			.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.FirstOrDefault();

		if (string.IsNullOrEmpty(firstToken))
		{
			return null;
		}

		string trimmed = firstToken.Trim(trimPunctuation);
		if (trimmed.EndsWith("'s", StringComparison.OrdinalIgnoreCase))
		{
			trimmed = trimmed[..^2];
		}

		trimmed = trimmed.Trim('\'', '‘', '’');
		return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
	}

	static async Task EnsureExtractedAsync(CancellationToken cancellationToken)
	{
		if (File.Exists(DatabasePath))
		{
			return;
		}

		await extractionLock.WaitAsync(cancellationToken);
		try
		{
			if (File.Exists(DatabasePath))
			{
				return;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
			string tempPath = DatabasePath + ".tmp";
			await using (Stream source = await FileSystem.OpenAppPackageFileAsync(assetPath))
			await using (FileStream target = File.Create(tempPath))
			{
				await source.CopyToAsync(target, cancellationToken);
			}

			File.Move(tempPath, DatabasePath, overwrite: true);
		}
		finally
		{
			extractionLock.Release();
		}
	}
}