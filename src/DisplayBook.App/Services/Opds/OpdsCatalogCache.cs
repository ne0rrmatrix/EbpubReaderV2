using System.Text.Json;
using System.Text.Json.Serialization;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

/// <summary>
/// JSON-file-backed cache of OPDS feeds (one file per feed, stored under
/// <c>Opds/Cache/</c> in the app data directory). Entries are keyed by the normalized feed
/// URL. The parser is stateless; this is the single place feeds are persisted.
/// </summary>
public sealed class OpdsCatalogCache : IOpdsCatalogCache
{
    private const string FolderName = "Cache";
    private const string FileNamePrefix = "feed-";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new StringDictionaryConverter(),
        },
    };

    private readonly string _folderPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OpdsCatalogCache(BookStorageService storage)
    {
        _folderPath = Path.Combine(storage.ContentRoot, FolderName);
    }

    public async Task<OpdsFeed?> GetAsync(string url, TimeSpan? maxAge = null, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(url);
        if (filePath == null || !File.Exists(filePath))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<OpdsFeedCacheEntry>(json, SerializerOptions);
            if (entry == null || string.IsNullOrEmpty(entry.SerializedFeed))
            {
                return null;
            }

            if (maxAge.HasValue && DateTime.UtcNow - entry.CachedAt > maxAge.Value)
            {
                return null;
            }

            var feed = JsonSerializer.Deserialize<OpdsFeed>(entry.SerializedFeed, SerializerOptions);
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
            _gate.Release();
        }
    }

    public async Task SetAsync(OpdsFeed feed, string url, string? parentPath = null, string? serverId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);

        var filePath = GetFilePath(url);
        if (filePath == null)
        {
            return;
        }

        var normalized = OpdsUrlNormalizer.Normalize(url);
        var entry = new OpdsFeedCacheEntry
        {
            CacheKey = fileNameFor(normalized),
            Url = normalized,
            Title = feed.Title ?? string.Empty,
            ParentPath = parentPath ?? string.Empty,
            ServerId = serverId,
            CachedAt = DateTime.UtcNow,
            EntryCount = feed.Entries?.Count ?? 0,
            SerializedFeed = JsonSerializer.Serialize(feed, SerializerOptions),
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_folderPath);
            var json = JsonSerializer.Serialize(entry, SerializerOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string url, CancellationToken cancellationToken = default)
    {
        var filePath = GetFilePath(url);
        if (filePath == null || !File.Exists(filePath))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_folderPath))
            {
                Directory.Delete(_folderPath, recursive: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? GetFilePath(string url)
    {
        var normalized = OpdsUrlNormalizer.Normalize(url);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return Path.Combine(_folderPath, fileNameFor(normalized));
    }

    private static string fileNameFor(string normalizedUrl)
    {
        var key = ComputeKey(normalizedUrl);
        return $"{FileNamePrefix}{key}.json";
    }

    private static string ComputeKey(string normalizedUrl)
    {
        if (Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
        {
            var hostPort = $"{uri.Host}:{uri.Port}";
            var path = uri.AbsolutePath.TrimEnd('/');
            var text = $"{hostPort}{path}";

            var bytes = System.Text.Encoding.UTF8.GetBytes(text.ToLowerInvariant());
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
        }

        var fallback = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedUrl.ToLowerInvariant())));
        return fallback[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Serializes <see cref="Dictionary{TKey,TValue}"/> with <see langword="string"/> keys and
/// <see langword="object"/> values as plain JSON (values written/read as strings), which is what
/// the OPDS parser stores in <c>ExtendedFeedMetadata</c> and <c>ExtendedMetadata</c>.
/// </summary>
internal sealed class StringDictionaryConverter : JsonConverter<Dictionary<string, object>>
{
    public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Cannot deserialize dictionary from token {reader.TokenType}.");
        }

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Unexpected token {reader.TokenType} while reading dictionary.");
            }

            var key = reader.GetString() ?? string.Empty;
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
        foreach (var pair in value)
        {
            writer.WritePropertyName(pair.Key);
            WriteValue(writer, pair.Value);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
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
