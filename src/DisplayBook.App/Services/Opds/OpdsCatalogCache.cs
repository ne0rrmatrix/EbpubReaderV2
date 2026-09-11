using System.Text.Json;
using System.Text.Json.Serialization;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

/// <summary>
/// JSON-file-backed cache of OPDS feeds (one file per feed, stored under
/// <c>Opds/Cache/</c> in the app data directory). Entries are keyed by the normalized feed
/// URL. The parser is stateless; this is the single place feeds are persisted.
/// </summary>
public sealed partial class OpdsCatalogCache(BookStorageService storage) : IOpdsCatalogCache, IDisposable
{
	const string folderName = "Cache";
	const string fileNamePrefix = "feed-";

	static readonly JsonSerializerOptions serializerOptions = new()
	{
		WriteIndented = false,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
		Converters =
		{
			new StringDictionaryConverter(),
		},
	};

	readonly string folderPath = Path.Combine(storage.ContentRoot, folderName);
	readonly SemaphoreSlim gate = new(1, 1);

	public async Task<OpdsFeed?> GetAsync(string url, TimeSpan? maxAge = null, CancellationToken cancellationToken = default)
	{
		string? filePath = GetFilePath(url);
		if (filePath == null || !File.Exists(filePath))
		{
			return null;
		}

		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!File.Exists(filePath))
			{
				return null;
			}

			string json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
			OpdsFeedCacheEntry? entry = JsonSerializer.Deserialize<OpdsFeedCacheEntry>(json, serializerOptions);
			if (entry == null || string.IsNullOrEmpty(entry.SerializedFeed))
			{
				return null;
			}

			if (maxAge.HasValue && DateTime.UtcNow - entry.CachedAt > maxAge.Value)
			{
				return null;
			}

			OpdsFeed? feed = JsonSerializer.Deserialize<OpdsFeed>(entry.SerializedFeed, serializerOptions);
			if (feed == null)
			{
				return null;
			}

			feed.Entries ??= [];
			return feed;
		}
		catch (Exception)
		{
			// A corrupt entry is skipped; the caller will re-fetch.
			return null;
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task SetAsync(OpdsFeed feed, string url, string? parentPath = null, string? serverId = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(feed);

		string? filePath = GetFilePath(url);
		if (filePath == null)
		{
			return;
		}

		string normalized = OpdsUrlNormalizer.Normalize(url);
		OpdsFeedCacheEntry entry = new()
		{
			CacheKey = fileNameFor(normalized),
			Url = normalized,
			Title = feed.Title ?? string.Empty,
			ParentPath = parentPath ?? string.Empty,
			ServerId = serverId,
			CachedAt = DateTime.UtcNow,
			EntryCount = feed.Entries?.Count ?? 0,
			SerializedFeed = JsonSerializer.Serialize(feed, serializerOptions),
		};

		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Directory.CreateDirectory(folderPath);
			string json = JsonSerializer.Serialize(entry, serializerOptions);
			await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task RemoveAsync(string url, CancellationToken cancellationToken = default)
	{
		string? filePath = GetFilePath(url);
		if (filePath == null || !File.Exists(filePath))
		{
			return;
		}

		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (File.Exists(filePath))
			{
				File.Delete(filePath);
			}
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task ClearAsync(CancellationToken cancellationToken = default)
	{
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (Directory.Exists(folderPath))
			{
				Directory.Delete(folderPath, recursive: true);
			}
		}
		finally
		{
			gate.Release();
		}
	}

	string? GetFilePath(string url)
	{
		string normalized = OpdsUrlNormalizer.Normalize(url);
		return string.IsNullOrEmpty(normalized) ? null : Path.Combine(folderPath, fileNameFor(normalized));
	}

	static string fileNameFor(string normalizedUrl)
	{
		string key = ComputeKey(normalizedUrl);
		return $"{fileNamePrefix}{key}.json";
	}

	static string ComputeKey(string normalizedUrl)
	{
		if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out Uri? uri))
		{
			string hostPort = $"{uri.Host}:{uri.Port}";
			string path = uri.AbsolutePath.TrimEnd('/');
			string text = $"{hostPort}{path}";

			byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant());
			byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
			return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
		}

		string fallback = Convert.ToHexString(
			System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedUrl.ToLowerInvariant())));
		return fallback[..16].ToLowerInvariant();
	}

	public void Dispose()
	{
		gate.Dispose();
	}
}

/// <summary>
/// Serializes <see cref="Dictionary{TKey,TValue}"/> with <see langword="string"/> keys and
/// <see langword="object"/> values as plain JSON (values written/read as strings), which is what
/// the OPDS parser stores in <c>ExtendedFeedMetadata</c> and <c>ExtendedMetadata</c>.
/// </summary>
sealed class StringDictionaryConverter : JsonConverter<Dictionary<string, object>>
{
	public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType != JsonTokenType.StartObject)
		{
			throw new JsonException($"Cannot deserialize dictionary from token {reader.TokenType}.");
		}

		Dictionary<string, object> result = new(StringComparer.OrdinalIgnoreCase);
		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			if (reader.TokenType != JsonTokenType.PropertyName)
			{
				throw new JsonException($"Unexpected token {reader.TokenType} while reading dictionary.");
			}

			string key = reader.GetString() ?? string.Empty;
			reader.Read();

			object value = reader.TokenType switch
			{
				JsonTokenType.Null => null!,
				JsonTokenType.String => reader.GetString() ?? string.Empty,
				JsonTokenType.True => true,
				JsonTokenType.False => false,
				JsonTokenType.Number => reader.GetDecimal(),
				_ => reader.GetString() ?? string.Empty,
			};

			result[key] = value;
		}

		return result;
	}

	public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();
		foreach (KeyValuePair<string, object> pair in value)
		{
			writer.WritePropertyName(pair.Key);
			WriteValue(writer, pair.Value);
		}

		writer.WriteEndObject();
	}

	static void WriteValue(Utf8JsonWriter writer, object? value)
	{
		switch (value)
		{
			case null:
				writer.WriteNullValue();
				break;
			case string s:
				writer.WriteStringValue(s);
				break;
			case bool b:
				writer.WriteBooleanValue(b);
				break;
			case int i:
				writer.WriteNumberValue(i);
				break;
			case long l:
				writer.WriteNumberValue(l);
				break;
			case decimal d:
				writer.WriteNumberValue(d);
				break;
			case double dbl:
				writer.WriteNumberValue(dbl);
				break;
			default:
				writer.WriteStringValue(value.ToString() ?? string.Empty);
				break;
		}
	}
}
