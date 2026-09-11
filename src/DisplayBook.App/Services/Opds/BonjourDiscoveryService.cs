using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using Microsoft.Extensions.Logging;
using Zeroconf;

namespace DisplayBook.App.Services;

/// <summary>
/// Client-only mDNS/Bonjour discovery for Calibre servers. DisplayBook never advertises
/// services itself; it scans for the <c>_calibre._tcp</c> service type that Calibre publishes
/// (name "Books in calibre", TXT record <c>path=/opds</c>) and turns each response into an
/// <see cref="OpdsServer"/> whose <see cref="OpdsServer.Url"/> is the server's OPDS feed URL.
/// Manual URL entry always works even when mDNS is unavailable.
/// </summary>
public sealed partial class BonjourDiscoveryService(ILogger<BonjourDiscoveryService> logger) : IBonjourDiscoveryService
{
	const string calibreServiceType = "_calibre._tcp.local.";
	static readonly string[] serviceTypes = [calibreServiceType];
	static readonly TimeSpan scanTimeout = TimeSpan.FromSeconds(5);
	static readonly TimeSpan scanInterval = TimeSpan.FromSeconds(10);
	const int missedScanGracePeriod = 3;
	const string defaultOpdsPath = "/opds";
	const string txtPathKey = "path";
	const string metadataServiceName = "mdnsServiceName";
	const string metadataHostName = "mdnsHostName";
	const string metadataPort = "mdnsPort";
	const string metadataPath = "mdnsPath";
#if ANDROID
	const string multicastLockTag = "DisplayBook Zeroconf lock";
#endif

	readonly ILogger<BonjourDiscoveryService> logger = logger;
	readonly ConcurrentDictionary<string, OpdsServer> servers = new();
	readonly ConcurrentDictionary<string, int> missedScans = new();
	CancellationTokenSource cts = new();
	Task? loopTask;
	int started;

	public bool IsDiscovering => Volatile.Read(ref cts) != null;

	public event EventHandler<DiscoveredServerEventArgs>? ServerDiscovered;

	public event EventHandler<ServerRemovedEventArgs>? ServerRemoved;

	public event EventHandler<DiscoveryErrorEventArgs>? DiscoveryError;

	public Task StartDiscoveryAsync(CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return Task.CompletedTask;
		}

		if (Interlocked.Exchange(ref started, 1) == 1)
		{
			return Task.CompletedTask;
		}

		cts = new CancellationTokenSource();
		loopTask = RunLoopAsync(cts.Token);
		return Task.CompletedTask;
	}

	public async Task StopDiscoveryAsync()
	{
		cts = Volatile.Read<CancellationTokenSource>(ref this.cts);
		Task? loop = loopTask;
		loopTask = null;

		if (cts != null)
		{
			await cts.CancelAsync().ConfigureAwait(false);
		}

		if (loop != null)
		{
			try
			{
				await loop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Expected when stopping discovery.
			}
			catch (Exception)
			{
				// The loop cleans up after itself; ignore failures during shutdown.
			}
		}

		Interlocked.Exchange(ref started, 0);

		// Intentionally keep the last-known snapshot so callers can still list servers
		// after discovery has been paused/stopped.
		missedScans.Clear();
	}

	public Task<IReadOnlyList<OpdsServer>> GetDiscoveredServersAsync()
	{
		List<OpdsServer> snapshot = [.. servers.Values.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)];
		return Task.FromResult<IReadOnlyList<OpdsServer>>(snapshot);
	}

	public void Dispose()
	{
		cts = Volatile.Read<CancellationTokenSource>(ref this.cts);
		Task? loop = loopTask;
		loopTask = null;

		cts?.Cancel();
		if (loop != null)
		{
			try
			{
				loop.GetAwaiter().GetResult();
			}
			catch (Exception)
			{
				// Dispose is best-effort.
			}
		}

		cts?.Dispose();
		servers.Clear();
		missedScans.Clear();
	}

	async Task RunLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await ScanOnceAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				RaiseError("Discovery scan failed.", ex);
			}

			try
			{
				await Task.Delay(scanInterval, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	async Task ScanOnceAsync(CancellationToken cancellationToken)
	{
#if ANDROID
		Android.Net.Wifi.WifiManager.MulticastLock? multicastLock = AcquireMulticastLock();
#endif
		try
		{
			IReadOnlyList<IZeroconfHost> hosts = await ZeroconfResolver
				.ResolveAsync(serviceTypes, scanTimeout, 2, 2000, null, cancellationToken)
				.ConfigureAwait(false);

			HashSet<string> seenKeys = [];
			foreach (IZeroconfHost? host in hosts)
			{
				if (host.Services.Count == 0)
				{
					continue;
				}

				string? address = GetPreferredAddress(host);
				if (address is null)
				{
					continue;
				}
				foreach (var item in host.Services.Select(service => service.Value).Where(serviceInfo => ResolvePort(serviceInfo) > 0))
				{
					IService serviceInfo = item;
					int port = ResolvePort(serviceInfo);
					string key = $"{host.Id}:{port}";
					seenKeys.Add(key);
					RegisterHostServer(key, host, address, port, serviceInfo);
				}
			}

			RemoveStaleServers(seenKeys);
		}
		catch (OperationCanceledException)
		{
			// Discovery is stopping.
		}
		catch (Exception ex)
		{
			RaiseError("mDNS scan failed.", ex);
		}
		finally
		{
#if ANDROID
			ReleaseMulticastLock(multicastLock);
#endif
		}
	}

	void RegisterHostServer(string key, IZeroconfHost host, string address, int port, IService service)
	{
		string path = GetPathFromProperties(service.Properties);
		string url = BuildFeedUrl(address, port, path);
		string name = GetName(host, service);

		bool isNew = false;
		if (!servers.TryGetValue(key, out OpdsServer? stored))
		{
			stored = new OpdsServer();
			isNew = true;
			servers[key] = stored;
		}

		bool changed = isNew || stored.Url != url || stored.Name != name;
		stored.Name = name;
		stored.Url = url;
		stored.Type = ServerType.Discovered;
		stored.LastSeen = DateTime.UtcNow;
		stored.Metadata[metadataServiceName] = service.ServiceName;
		stored.Metadata[metadataHostName] = host.DisplayName;
		stored.Metadata[metadataPort] = port.ToString(CultureInfo.InvariantCulture);
		stored.Metadata[metadataPath] = path;

		missedScans.TryRemove(key, out _);

		if (changed)
		{
			ServerDiscovered?.Invoke(this, new DiscoveredServerEventArgs(stored, isNew));
		}
	}

	void RemoveStaleServers(HashSet<string> seenKeys)
	{
		List<string> removedKeys = [.. servers.Keys
			.Where(key => !seenKeys.Contains(key))
			.Where(key =>
			{
				int missed = missedScans.GetValueOrDefault(key) + 1;
				if (missed < missedScanGracePeriod)
				{
					missedScans[key] = missed;
					return false;
				}

				return true;
			})];

		foreach (string key in removedKeys)
		{
			if (servers.TryRemove(key, out OpdsServer? stale))
			{
				missedScans.TryRemove(key, out _);
				ServerRemoved?.Invoke(this, new ServerRemovedEventArgs(stale.Url, stale.Name));
			}
		}
	}

	static string? GetPreferredAddress(IZeroconfHost host)
	{
		string? firstNonLoopback = null;
		string? firstIpv4 = null;

		foreach (string? address in host.IPAddresses)
		{
			if (IsLoopbackAddress(address))
			{
				continue;
			}

			firstNonLoopback ??= address;

			if (firstIpv4 == null && IsIpv4(address))
			{
				firstIpv4 = address;
			}
		}

		return firstIpv4 ?? firstNonLoopback;
	}

	static bool IsLoopbackAddress(string address)
	{
		// .NET 10: IPAddress.IsLoopback is a static method, not a property.
		return IPAddress.TryParse(address, out IPAddress? parsed) && IPAddress.IsLoopback(parsed);
	}

	static bool IsIpv4(string address)
	{
		return IPAddress.TryParse(address, out IPAddress? parsed)
			&& parsed.AddressFamily == AddressFamily.InterNetwork;
	}

	static int ResolvePort(IService service)
	{
		return service.Port > 0 ? service.Port : GetPortFromServiceName(service.ServiceName);
	}

	/// <summary>
	/// Fallback for responders that omit the SRV port: the port is the trailing segment of the
	/// SRV instance name, e.g. "calibre:_calibre._tcp.local.:8080" → 8080.
	/// </summary>
	static int GetPortFromServiceName(string serviceName)
	{
		if (string.IsNullOrWhiteSpace(serviceName))
		{
			return 0;
		}

		int index = serviceName.LastIndexOf(':');
		if(index < 0 || index == serviceName.Length - 1)
		{
			return 0;
		}
		return int.TryParse(serviceName[(index + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
			? port
			: 0;
	}

	static string GetPathFromProperties(IReadOnlyList<IReadOnlyDictionary<string, string>> properties)
	{
		string? path = properties
			.SelectMany(set => set)
			.Where(entry => string.Equals(entry.Key, txtPathKey, StringComparison.OrdinalIgnoreCase)
				&& !string.IsNullOrWhiteSpace(entry.Value))
			.Select(entry => entry.Value)
			.FirstOrDefault();

		return string.IsNullOrWhiteSpace(path) ? defaultOpdsPath : path;
	}

	static string BuildFeedUrl(string address, int port, string path)
	{
		path = path.Trim();
		if (path.Length == 0)
		{
			path = defaultOpdsPath;
		}

		if (!path.StartsWith('/'))
		{
			path = $"/{path}";
		}

		return $"http://{address}:{port}{path}";
	}

	static string GetName(IZeroconfHost host, IService service)
	{
		return string.IsNullOrWhiteSpace(host.DisplayName) ? service.ServiceName : host.DisplayName;
	}

	void RaiseError(string message, Exception error)
	{
		logger.LogWarning(error, "OPDS mDNS discovery error: {Message}", message);
		DiscoveryError?.Invoke(this, new DiscoveryErrorEventArgs(message, error));
	}

#if ANDROID
	static Android.Net.Wifi.WifiManager.MulticastLock? AcquireMulticastLock()
	{
		try
		{
			Android.Net.Wifi.WifiManager? wifiManager = (Android.Net.Wifi.WifiManager?)Android.App.Application.Context
				.GetSystemService(Android.Content.Context.WifiService);

			if (wifiManager == null)
			{
				return null;
			}

			Android.Net.Wifi.WifiManager.MulticastLock? lockHandle = wifiManager.CreateMulticastLock(multicastLockTag);
			lockHandle?.Acquire();
			return lockHandle;
		}
		catch (Exception)
		{
			// Multicast lock unavailable; the scan proceeds without it (best effort).
			return null;
		}
	}

	static void ReleaseMulticastLock(Android.Net.Wifi.WifiManager.MulticastLock? lockHandle)
	{
		if (lockHandle == null)
		{
			return;
		}

		try
		{
			lockHandle.Release();
		}
		catch (Exception)
		{
			// Best effort release.
		}
	}
#endif
}
