using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

/// <summary>
/// Persists OPDS server profiles, including servers saved automatically after discovery. Backed by
/// a JSON file in the app's data directory.
/// </summary>
public interface IOpdsServerRepository
{
    /// <summary>
    /// All stored servers, ordered by name.
    /// </summary>
    Task<IReadOnlyList<OpdsServer>> GetAllAsync();

    /// <summary>
    /// Adds a new server. The repository assigns storage responsibility; callers keep using
    /// the same instance afterwards.
    /// </summary>
    Task<OpdsServer> AddAsync(OpdsServer server);

    /// <summary>
    /// Replaces the stored server with the given id; a no-op when the id is unknown.
    /// </summary>
    Task UpdateAsync(string serverId, OpdsServer server);

    /// <summary>
    /// Removes a server by id.
    /// </summary>
    Task RemoveAsync(string serverId);

    /// <summary>
    /// Finds a stored server by id (case-insensitive).
    /// </summary>
    OpdsServer? GetById(string serverId);

    /// <summary>
    /// Finds a stored server by URL, comparing scheme, host, port and path
    /// (trailing slashes ignored).
    /// </summary>
    OpdsServer? FindByUrl(string url);
}
