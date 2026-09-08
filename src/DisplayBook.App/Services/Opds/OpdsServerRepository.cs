using System.Text.Json;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

/// <summary>
/// JSON-file-backed store for OPDS server profiles. The file lives in the app data directory
/// (<c>Opds/servers.json</c>) and is loaded into memory on first access; every mutation writes
/// through. All public methods are safe to call from any thread.
/// </summary>
public sealed class OpdsServerRepository : IOpdsServerRepository
{
    private const string FileName = "servers.json";
    private const string FolderName = "Opds";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<OpdsServer>? _servers;

    public OpdsServerRepository(BookStorageService storage)
    {
        _filePath = Path.Combine(storage.ContentRoot, FolderName, FileName);
    }

    public async Task<IReadOnlyList<OpdsServer>> GetAllAsync()
    {
        var servers = await LoadAsync().ConfigureAwait(false);
        return servers
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<OpdsServer> AddAsync(OpdsServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var servers = GetOrLoad();
            var stored = CloneWithId(server, string.IsNullOrWhiteSpace(server.Id) ? Guid.NewGuid().ToString("N") : server.Id);
            if (FindById(servers, stored.Id) is null)
            {
                servers.Add(stored);
                await SaveAsync(servers).ConfigureAwait(false);
            }

            return stored;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(string serverId, OpdsServer server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        ArgumentNullException.ThrowIfNull(server);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var servers = GetOrLoad();
            var index = FindIndexById(servers, serverId);
            if (index < 0)
            {
                return;
            }

            var updated = CloneWithId(server, serverId);
            servers[index] = updated;
            await SaveAsync(servers).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var servers = GetOrLoad();
            if (servers.RemoveAll(item => item.Id == serverId) > 0)
            {
                await SaveAsync(servers).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public OpdsServer? GetById(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return null;
        }

        return FindById(GetOrLoad(), serverId);
    }

    public OpdsServer? FindByUrl(string url)
    {
        var normalized = OpdsUrlNormalizer.Normalize(url);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var servers = GetOrLoad();
        return servers.FirstOrDefault(server => OpdsUrlNormalizer.Normalize(server.Url) == normalized);
    }

    private async Task<List<OpdsServer>> LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return GetOrLoad();
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<OpdsServer> GetOrLoad()
    {
        var cached = _servers;
        if (cached != null)
        {
            return cached;
        }

        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<List<OpdsServer>>(json, SerializerOptions);
                _servers = NormalizeLoaded(loaded);
            }
            else
            {
                _servers = new List<OpdsServer>();
            }
        }
        catch (Exception)
        {
            // A corrupt profile file must not take the app down; start fresh and let the
            // next mutation overwrite it.
            _servers = new List<OpdsServer>();
        }

        return _servers;
    }

    private static List<OpdsServer> NormalizeLoaded(List<OpdsServer>? loaded)
    {
        var result = new List<OpdsServer>();
        if (loaded == null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in loaded)
        {
            if (string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Url))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(server.Id))
            {
                server.Id = Guid.NewGuid().ToString("N");
            }

            if (seen.Add(server.Id))
            {
                result.Add(server);
            }
        }

        return result;
    }

    private static OpdsServer CloneWithId(OpdsServer source, string id)
    {
        return new OpdsServer
        {
            Id = id,
            Name = source.Name,
            Url = source.Url,
            Type = source.Type,
            LastSeen = source.LastSeen,
            IsEnabled = source.IsEnabled,
            Username = source.Username,
            Password = source.Password,
            ApiKey = source.ApiKey,
            Metadata = new Dictionary<string, string>(source.Metadata ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
        };
    }

    private static OpdsServer? FindById(List<OpdsServer> servers, string id)
    {
        return servers.FirstOrDefault(server => server.Id == id);
    }

    private static int FindIndexById(List<OpdsServer> servers, string id)
    {
        for (var index = 0; index < servers.Count; index++)
        {
            if (servers[index].Id == id)
            {
                return index;
            }
        }

        return -1;
    }

    private async Task SaveAsync(List<OpdsServer> servers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.Serialize(servers, SerializerOptions);
        await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        _servers = new List<OpdsServer>(servers);
    }
}
