using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
public sealed class BonjourDiscoveryService(ILogger<BonjourDiscoveryService> logger) : IBonjourDiscoveryService
{
    private const string CalibreServiceType = "_calibre._tcp.local.";
    private static readonly string[] ServiceTypes = [CalibreServiceType];
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    private const int MissedScanGracePeriod = 3;
    private const string DefaultOpdsPath = "/opds";
    private const string TxtPathKey = "path";
    private const string MetadataServiceName = "mdnsServiceName";
    private const string MetadataHostName = "mdnsHostName";
    private const string MetadataPort = "mdnsPort";
    private const string MetadataPath = "mdnsPath";
#if ANDROID
    private const string MulticastLockTag = "DisplayBook Zeroconf lock";
#endif

    private readonly ILogger<BonjourDiscoveryService> _logger = logger;
    private readonly ConcurrentDictionary<string, OpdsServer> _servers = new();
    private readonly ConcurrentDictionary<string, int> _missedScans = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private int _started;

    public bool IsDiscovering => Volatile.Read(ref _cts) != null;

    public event EventHandler<DiscoveredServerEventArgs>? ServerDiscovered;

    public event EventHandler<ServerRemovedEventArgs>? ServerRemoved;

    public event EventHandler<DiscoveryErrorEventArgs>? DiscoveryError;

    public Task StartDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _loopTask = RunLoopAsync(cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopDiscoveryAsync()
    {
        var cts = Volatile.Read(ref _cts);
        var loop = _loopTask;
        _cts = null;
        _loopTask = null;

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

        Interlocked.Exchange(ref _started, 0);

        // Intentionally keep the last-known snapshot so callers can still list servers
        // after discovery has been paused/stopped.
        _missedScans.Clear();
    }

    public Task<IReadOnlyList<OpdsServer>> GetDiscoveredServersAsync()
    {
        var snapshot = _servers.Values
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<OpdsServer>>(snapshot);
    }

    public void Dispose()
    {
        var cts = Volatile.Read(ref _cts);
        var loop = _loopTask;
        _cts = null;
        _loopTask = null;

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
        _servers.Clear();
        _missedScans.Clear();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
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
                await Task.Delay(ScanInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
#if ANDROID
        var multicastLock = AcquireMulticastLock();
#endif
        try
        {
            var hosts = await ZeroconfResolver
                .ResolveAsync(ServiceTypes, ScanTimeout, 2, 2000, null, cancellationToken)
                .ConfigureAwait(false);

            var seenKeys = new HashSet<string>();
            foreach (var host in hosts)
            {
                if (host.Services.Count == 0)
                {
                    continue;
                }

                var address = GetPreferredAddress(host);
                if (address == null)
                {
                    continue;
                }

                foreach (var service in host.Services)
                {
                    var serviceInfo = service.Value;
                    var port = ResolvePort(serviceInfo);
                    if (port <= 0)
                    {
                        continue;
                    }

                    var key = $"{host.Id}:{port}";
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

    private void RegisterHostServer(string key, IZeroconfHost host, string address, int port, IService service)
    {
        var path = GetPathFromProperties(service.Properties);
        var url = BuildFeedUrl(address, port, path);
        var name = GetName(host, service);

        OpdsServer? stored;
        var isNew = false;
        if (!_servers.TryGetValue(key, out stored))
        {
            stored = new OpdsServer();
            isNew = true;
            _servers[key] = stored;
        }

        var changed = isNew || stored.Url != url || stored.Name != name;
        stored.Name = name;
        stored.Url = url;
        stored.Type = ServerType.Discovered;
        stored.LastSeen = DateTime.UtcNow;
        stored.Metadata[MetadataServiceName] = service.ServiceName;
        stored.Metadata[MetadataHostName] = host.DisplayName;
        stored.Metadata[MetadataPort] = port.ToString(CultureInfo.InvariantCulture);
        stored.Metadata[MetadataPath] = path;

        _missedScans.TryRemove(key, out _);

        if (changed)
        {
            ServerDiscovered?.Invoke(this, new DiscoveredServerEventArgs(stored, isNew));
        }
    }

    private void RemoveStaleServers(HashSet<string> seenKeys)
    {
        var removedKeys = _servers.Keys
            .Where(key => !seenKeys.Contains(key))
            .Where(key =>
            {
                var missed = _missedScans.GetValueOrDefault(key) + 1;
                if (missed < MissedScanGracePeriod)
                {
                    _missedScans[key] = missed;
                    return false;
                }

                return true;
            })
            .ToList();

        foreach (var key in removedKeys)
        {
            OpdsServer? stale;
            if (_servers.TryRemove(key, out stale))
            {
                _missedScans.TryRemove(key, out _);
                ServerRemoved?.Invoke(this, new ServerRemovedEventArgs(stale.Url, stale.Name));
            }
        }
    }

    private static string? GetPreferredAddress(IZeroconfHost host)
    {
        string? firstNonLoopback = null;
        string? firstIpv4 = null;

        foreach (var address in host.IPAddresses)
        {
            if (IsLoopbackAddress(address))
            {
                continue;
            }

            if (firstNonLoopback == null)
            {
                firstNonLoopback = address;
            }

            if (firstIpv4 == null && IsIpv4(address))
            {
                firstIpv4 = address;
            }
        }

        return firstIpv4 ?? firstNonLoopback;
    }

    private static bool IsLoopbackAddress(string address)
    {
        // .NET 10: IPAddress.IsLoopback is a static method, not a property.
        return IPAddress.TryParse(address, out var parsed) && IPAddress.IsLoopback(parsed);
    }

    private static bool IsIpv4(string address)
    {
        return IPAddress.TryParse(address, out var parsed)
            && parsed.AddressFamily == AddressFamily.InterNetwork;
    }

    private static int ResolvePort(IService service)
    {
        if (service.Port > 0)
        {
            return service.Port;
        }

        return GetPortFromServiceName(service.ServiceName);
    }

    /// <summary>
    /// Fallback for responders that omit the SRV port: the port is the trailing segment of the
    /// SRV instance name, e.g. "calibre:_calibre._tcp.local.:8080" → 8080.
    /// </summary>
    private static int GetPortFromServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return 0;
        }

        var index = serviceName.LastIndexOf(':');
        if (index < 0 || index == serviceName.Length - 1)
        {
            return 0;
        }

        return int.TryParse(serviceName[(index + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? port
            : 0;
    }

    private static string GetPathFromProperties(IReadOnlyList<IReadOnlyDictionary<string, string>> properties)
    {
        var path = properties
            .SelectMany(set => set)
            .Where(entry => string.Equals(entry.Key, TxtPathKey, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Value)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(path) ? DefaultOpdsPath : path;
    }

    private static string BuildFeedUrl(string address, int port, string path)
    {
        path = path.Trim();
        if (path.Length == 0)
        {
            path = DefaultOpdsPath;
        }

        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            path = $"/{path}";
        }

        return $"http://{address}:{port}{path}";
    }

    private static string GetName(IZeroconfHost host, IService service)
    {
        return string.IsNullOrWhiteSpace(host.DisplayName) ? service.ServiceName : host.DisplayName;
    }

    private void RaiseError(string message, Exception error)
    {
        _logger.LogWarning(error, "OPDS mDNS discovery error: {Message}", message);
        DiscoveryError?.Invoke(this, new DiscoveryErrorEventArgs(message, error));
    }

#if ANDROID
    private static Android.Net.Wifi.WifiManager.MulticastLock? AcquireMulticastLock()
    {
        try
        {
            var wifiManager = (Android.Net.Wifi.WifiManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.WifiService);

            if (wifiManager == null)
            {
                return null;
            }

            var lockHandle = wifiManager.CreateMulticastLock(MulticastLockTag);
            lockHandle?.Acquire();
            return lockHandle;
        }
        catch (Exception)
        {
            // Multicast lock unavailable; the scan proceeds without it (best effort).
            return null;
        }
    }

    private static void ReleaseMulticastLock(Android.Net.Wifi.WifiManager.MulticastLock? lockHandle)
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
