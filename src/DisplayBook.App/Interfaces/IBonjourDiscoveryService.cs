using DisplayBook.App.Models;

namespace DisplayBook.App.Interfaces;

/// <summary>
/// Raises when a server is first seen on the network. Events are raised on a background thread;
/// subscribers must marshal to the UI thread as needed.
/// </summary>
public sealed class DiscoveredServerEventArgs(OpdsServer server, bool isNew) : EventArgs
{
	/// <summary>
	/// The discovered server (base OPDS feed URL, display name, mDNS metadata).
	/// </summary>
	public OpdsServer Server { get; } = server;

	/// <summary>
	/// <see langword="true"/> when this is the first time the server was seen;
	/// <see langword="false"/> when an already-known server was refreshed.
	/// </summary>
	public bool IsNew { get; } = isNew;
}

/// <summary>
/// Raises when a previously discovered server has stopped responding to mDNS scans.
/// </summary>
public sealed class ServerRemovedEventArgs(string serverUrl, string name) : EventArgs
{
	public string ServerUrl { get; } = serverUrl;

	public string Name { get; } = name;
}

/// <summary>
/// Raises when the discovery loop encounters an error. The loop keeps running;
/// the UI may surface a transient warning.
/// </summary>
public sealed class DiscoveryErrorEventArgs(string message, Exception? error = null) : EventArgs
{
	public string Message { get; } = message;

	public Exception? Error { get; } = error;
}

/// <summary>
/// Discovers Calibre content servers (and other OPDS servers) advertising themselves on the
/// local network via Bonjour/mDNS. This is a client-only service: DisplayBook never advertises
/// services itself.
/// </summary>
public interface IBonjourDiscoveryService : IDisposable
{
	/// <summary>
	/// Whether the discovery loop is currently running.
	/// </summary>
	bool IsDiscovering { get; }

	/// <summary>
	/// Raised (on a background thread) when a server is discovered or refreshed.
	/// </summary>
	event EventHandler<DiscoveredServerEventArgs>? ServerDiscovered;

	/// <summary>
	/// Raised (on a background thread) when a previously seen server has gone away.
	/// </summary>
	event EventHandler<ServerRemovedEventArgs>? ServerRemoved;

	/// <summary>
	/// Raised (on a background thread) when a scan fails. Discovery continues.
	/// </summary>
	event EventHandler<DiscoveryErrorEventArgs>? DiscoveryError;

	/// <summary>
	/// Starts the discovery loop. Idempotent: returns immediately when discovery is already running.
	/// </summary>
	Task StartDiscoveryAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Stops the discovery loop and releases platform resources (e.g. the Android multicast lock).
	/// </summary>
	Task StopDiscoveryAsync();

	/// <summary>
	/// Returns a snapshot of all servers currently seen on the network.
	/// </summary>
	Task<IReadOnlyList<OpdsServer>> GetDiscoveredServersAsync();
}