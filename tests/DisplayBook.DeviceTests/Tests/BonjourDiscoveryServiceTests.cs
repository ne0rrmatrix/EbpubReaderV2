using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.DeviceTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Zeroconf;

namespace DisplayBook.DeviceTests.Tests;

/// <summary>
/// mDNS discovery with the network query replaced: each scan answers with the next scripted list
/// of hosts (the last one repeats), so what a Calibre responder advertises can be controlled.
/// </summary>
public sealed partial class BonjourDiscoveryServiceTests : IDisposable
{
	readonly List<Func<IReadOnlyList<IZeroconfHost>>> scans = [];
	readonly List<DiscoveredServerEventArgs> discovered = [];
	readonly List<ServerRemovedEventArgs> removed = [];
	readonly List<DiscoveryErrorEventArgs> errors = [];
	int scanCount;
	bool disposed;
	readonly BonjourDiscoveryService discovery;

	public BonjourDiscoveryServiceTests()
	{
		discovery = new BonjourDiscoveryService(NullLogger<BonjourDiscoveryService>.Instance, ResolveAsync, TimeSpan.FromMilliseconds(20));
		discovery.ServerDiscovered += (_, e) => Record(discovered, e);
		discovery.ServerRemoved += (_, e) => Record(removed, e);
		discovery.DiscoveryError += (_, e) => Record(errors, e);
	}

	// BonjourDiscoveryService.Dispose throws if called twice, so a test that disposes it itself
	// says so.
	public void Dispose()
	{
		if (!disposed)
		{
			discovery.Dispose();
		}
	}

	Task<IReadOnlyList<IZeroconfHost>> ResolveAsync(CancellationToken cancellationToken)
	{
		int index = Interlocked.Increment(ref scanCount) - 1;
		Func<IReadOnlyList<IZeroconfHost>> scan;
		lock (scans)
		{
			scan = scans[Math.Min(index, scans.Count - 1)];
		}

		return Task.FromResult(scan());
	}

	static void Record<T>(List<T> list, T item)
	{
		lock (list)
		{
			list.Add(item);
		}
	}

	static T[] Snapshot<T>(List<T> list)
	{
		lock (list)
		{
			return [.. list];
		}
	}

	void Scans(params IReadOnlyList<IZeroconfHost>[] results)
	{
		lock (scans)
		{
			scans.AddRange(results.Select(result => (Func<IReadOnlyList<IZeroconfHost>>)(() => result)));
		}
	}

	static FakeHost Host(string id, string displayName, string[] addresses, params FakeService[] services) =>
		new(id, displayName, addresses, services.ToDictionary(service => service.ServiceName, service => (IService)service));

	static FakeService Calibre(int port = 8080, string? path = "/opds", string serviceName = "Books in calibre._calibre._tcp.local.") =>
		new(serviceName, port, path is null ? [] : [new Dictionary<string, string> { ["path"] = path }]);

	async Task StartAndWaitForScansAsync(int scans)
	{
		await discovery.StartDiscoveryAsync();
		await Wait.UntilAsync(() => Volatile.Read(ref scanCount) >= scans, $"{scans} scans");
	}

	[Fact]
	public async Task Scan_TurnsACalibreResponderIntoAServer()
	{
		Scans([Host("host-1", "Desk calibre", ["::1", "fe80::1", "192.168.1.20"], Calibre(path: "opds"))]);

		await StartAndWaitForScansAsync(2);

		DiscoveredServerEventArgs found = Assert.Single(Snapshot(discovered));
		Assert.True(found.IsNew);
		OpdsServer server = found.Server;
		Assert.Equal(("Desk calibre", "http://192.168.1.20:8080/opds", ServerType.Discovered), (server.Name, server.Url, server.Type));
		Assert.Equal("8080", server.Metadata["mdnsPort"]);
		Assert.Equal("opds", server.Metadata["mdnsPath"]);
		Assert.Equal([server], await discovery.GetDiscoveredServersAsync());
	}

	[Fact]
	public async Task Scan_PicksAddressPortAndPathFallbacks()
	{
		Scans([
			Host("no-display-name", "", ["fe80::2", "10.0.0.4"], Calibre(port: 9090, path: null)),
			Host("port-in-name", "From name", ["10.0.0.5"], Calibre(port: 0, path: "  ", serviceName: "calibre:_calibre._tcp.local.:8081")),
			Host("no-port", "No port", ["10.0.0.6"], Calibre(port: 0, serviceName: "calibre._calibre._tcp.local.")),
			Host("trailing-colon", "Trailing colon", ["10.0.0.7"], Calibre(port: 0, serviceName: "calibre:")),
			Host("loopback-only", "Loopback", ["127.0.0.1", "::1"], Calibre()),
			Host("no-services", "Nothing", ["10.0.0.8"]),
		]);

		await StartAndWaitForScansAsync(2);

		Assert.Equal(
			["http://10.0.0.4:9090/opds", "http://10.0.0.5:8081/opds"],
			Snapshot(discovered).Select(e => e.Server.Url).Order());
		Assert.Contains(Snapshot(discovered), e => e.Server.Name == "Books in calibre._calibre._tcp.local.");
	}

	[Fact]
	public async Task Rescan_ReportsOnlyChanges()
	{
		Scans(
			[Host("host-1", "Desk", ["192.168.1.20"], Calibre())],
			[Host("host-1", "Desk", ["192.168.1.20"], Calibre())],
			[Host("host-1", "Renamed desk", ["192.168.1.20"], Calibre())]);

		await StartAndWaitForScansAsync(4);

		DiscoveredServerEventArgs[] events = Snapshot(discovered);
		Assert.Equal(2, events.Length);
		Assert.True(events[0].IsNew);
		Assert.False(events[1].IsNew);
		Assert.Equal("Renamed desk", events[1].Server.Name);
	}

	[Fact]
	public async Task ServerMissingForThreeScans_IsRemoved()
	{
		Scans([Host("host-1", "Desk", ["192.168.1.20"], Calibre())], []);

		await StartAndWaitForScansAsync(5);

		ServerRemovedEventArgs gone = Assert.Single(Snapshot(removed));
		Assert.Equal(("http://192.168.1.20:8080/opds", "Desk"), (gone.ServerUrl, gone.Name));
		Assert.Empty(await discovery.GetDiscoveredServersAsync());
	}

	[Fact]
	public async Task FailingScan_IsReportedAndScanningContinues()
	{
		lock (scans)
		{
			scans.Add(() => throw new System.Net.Sockets.SocketException(10013));
			scans.Add(() => [Host("host-1", "Desk", ["192.168.1.20"], Calibre())]);
		}

		await StartAndWaitForScansAsync(2);
		await Wait.UntilAsync(() => Snapshot(discovered).Length == 1, "the next scan to find the server");

		DiscoveryErrorEventArgs error = Snapshot(errors)[0];
		Assert.Equal("mDNS scan failed.", error.Message);
		Assert.IsType<System.Net.Sockets.SocketException>(error.Error);
	}

	[Fact]
	public async Task StartStop_AreIdempotentAndKeepTheLastResults()
	{
		Scans([Host("host-1", "Desk", ["192.168.1.20"], Calibre())]);

		await discovery.StartDiscoveryAsync(new CancellationToken(canceled: true));
		Assert.Equal(0, Volatile.Read(ref scanCount));

		await StartAndWaitForScansAsync(1);
		await discovery.StartDiscoveryAsync();
		await discovery.StopDiscoveryAsync();
		int scansAtStop = Volatile.Read(ref scanCount);
		await Task.Delay(100);

		Assert.Equal(scansAtStop, Volatile.Read(ref scanCount));
		Assert.Single(await discovery.GetDiscoveredServersAsync());

		await StartAndWaitForScansAsync(scansAtStop + 1);
		discovery.Dispose();
		disposed = true;
		Assert.Empty(await discovery.GetDiscoveredServersAsync());
	}

	sealed record FakeHost(string Id, string DisplayName, IReadOnlyList<string> IPAddresses, IReadOnlyDictionary<string, IService> Services) : IZeroconfHost
	{
		public string IPAddress => IPAddresses.FirstOrDefault() ?? string.Empty;
	}

	sealed record FakeService(string ServiceName, int Port, IReadOnlyList<IReadOnlyDictionary<string, string>> Properties) : IService
	{
		public string Name => ServiceName;

		public int Ttl => 120;
	}
}
