using System.Text.Json;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

/// <summary>
/// JSON-file-backed store for OPDS server profiles. The file lives in the app data directory
/// (<c>Opds/servers.json</c>) and is loaded into memory on first access; every mutation writes
/// through. All public methods are safe to call from any thread.
/// </summary>
public sealed partial class OpdsServerRepository(BookStorageService storage) : IOpdsServerRepository, IDisposable
{
	const string fileName = "servers.json";
	const string folderName = "Opds";

	static readonly JsonSerializerOptions serializerOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
	};

	readonly string filePath = Path.Combine(storage.ContentRoot, folderName, fileName);
	readonly SemaphoreSlim gate = new(1, 1);
	List<OpdsServer>? servers;
	bool disposedValue;

	public async Task<IReadOnlyList<OpdsServer>> GetAllAsync()
	{
		servers = await LoadAsync().ConfigureAwait(false);
		return [.. servers.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)];
	}

	public async Task<OpdsServer> AddAsync(OpdsServer server)
	{
		ArgumentNullException.ThrowIfNull(server);

		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			servers = GetOrLoad();
			OpdsServer stored = CloneWithId(server, string.IsNullOrWhiteSpace(server.Id) ? Guid.NewGuid().ToString("N") : server.Id);
			if (FindById(servers, stored.Id) is null)
			{
				servers.Add(stored);
				await SaveAsync(servers).ConfigureAwait(false);
			}

			return stored;
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task UpdateAsync(string serverId, OpdsServer server)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
		ArgumentNullException.ThrowIfNull(server);

		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			servers = GetOrLoad();
			int index = FindIndexById(servers, serverId);
			if (index < 0)
			{
				return;
			}

			OpdsServer updated = CloneWithId(server, serverId);
			servers[index] = updated;
			await SaveAsync(servers).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task RemoveAsync(string serverId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			servers = GetOrLoad();
			if (servers.RemoveAll(item => item.Id == serverId) > 0)
			{
				await SaveAsync(servers).ConfigureAwait(false);
			}
		}
		finally
		{
			gate.Release();
		}
	}

	public OpdsServer? GetById(string serverId)
	{
		return string.IsNullOrWhiteSpace(serverId) ? null : FindById(GetOrLoad(), serverId);
	}

	public OpdsServer? FindByUrl(string url)
	{
		string normalized = OpdsUrlNormalizer.Normalize(url);
		if (string.IsNullOrEmpty(normalized))
		{
			return null;
		}

		servers = GetOrLoad();
		return servers.FirstOrDefault(server => OpdsUrlNormalizer.Normalize(server.Url) == normalized);
	}

	async Task<List<OpdsServer>> LoadAsync()
	{
		await gate.WaitAsync().ConfigureAwait(false);
		try
		{
			return GetOrLoad();
		}
		finally
		{
			gate.Release();
		}
	}

	List<OpdsServer> GetOrLoad()
	{
		List<OpdsServer>? cached = servers;
		if (cached != null)
		{
			return cached;
		}

		try
		{
			if (File.Exists(filePath))
			{
				string json = File.ReadAllText(filePath);
				List<OpdsServer>? loaded = JsonSerializer.Deserialize<List<OpdsServer>>(json, serializerOptions);
				servers = NormalizeLoaded(loaded);
			}
			else
			{
				servers = [];
			}
		}
		catch (Exception)
		{
			// A corrupt profile file must not take the app down; start fresh and let the
			// next mutation overwrite it.
			servers = [];
		}

		return servers;
	}

	static List<OpdsServer> NormalizeLoaded(List<OpdsServer>? loaded)
	{
		List<OpdsServer> result = new();
		if (loaded == null)
		{
			return result;
		}

		HashSet<string> seen = new(StringComparer.Ordinal);
		foreach (OpdsServer server in loaded)
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

	static OpdsServer CloneWithId(OpdsServer source, string id)
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

	static OpdsServer? FindById(List<OpdsServer> servers, string id)
	{
		return servers.FirstOrDefault(server => server.Id == id);
	}

	static int FindIndexById(List<OpdsServer> servers, string id)
	{
		for (int index = 0; index < servers.Count; index++)
		{
			if (servers[index].Id == id)
			{
				return index;
			}
		}

		return -1;
	}

	async Task SaveAsync(List<OpdsServer> servers)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
		string json = JsonSerializer.Serialize(servers, serializerOptions);
		await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
		this.servers = [.. servers];
	}

	void Dispose(bool disposing)
	{
		if (!disposedValue)
		{
			if (disposing)
			{
				gate.Dispose();
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