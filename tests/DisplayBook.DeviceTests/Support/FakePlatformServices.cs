using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// Stands in for the system file and folder pickers, which need a person to choose something.
/// A test sets the next result before triggering an import.
/// </summary>
sealed class FakeBookPicker : IBookPickerService
{
	public static string? NextFile { get; set; }

	public static string? NextFolder { get; set; }

	public static void Reset()
	{
		NextFile = null;
		NextFolder = null;
	}

	public Task<FileResult?> PickEpubFileAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult(NextFile is null ? null : new FileResult(NextFile));

	public Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress, CancellationToken cancellationToken) =>
		Task.FromResult(NextFolder);
}

/// <summary>
/// Stands in for mDNS discovery, which depends on whatever happens to be on the local network.
/// <see cref="DisplayBook.App.Services.BonjourDiscoveryService"/> itself is tested directly in BonjourDiscoveryServiceTests.
/// </summary>
sealed partial class FakeBonjourDiscovery : IBonjourDiscoveryService
{
	public static Exception? StartFailure { get; set; }

	public bool IsDiscovering { get; private set; }

	public event EventHandler<DiscoveredServerEventArgs>? ServerDiscovered;
	public event EventHandler<ServerRemovedEventArgs>? ServerRemoved;
	public event EventHandler<DiscoveryErrorEventArgs>? DiscoveryError;

	public int ServerDiscoveredListenerCount => ServerDiscovered?.GetInvocationList().Length ?? 0;

	public Task StartDiscoveryAsync(CancellationToken cancellationToken = default)
	{
		if (StartFailure is { } failure)
		{
			throw failure;
		}

		IsDiscovering = true;
		return Task.CompletedTask;
	}

	public Task StopDiscoveryAsync()
	{
		IsDiscovering = false;
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<OpdsServer>> GetDiscoveredServersAsync() => Task.FromResult<IReadOnlyList<OpdsServer>>([]);

	public void RaiseDiscovered(OpdsServer server, bool isNew) => ServerDiscovered?.Invoke(this, new DiscoveredServerEventArgs(server, isNew));

	public void RaiseRemoved(string url, string name) => ServerRemoved?.Invoke(this, new ServerRemovedEventArgs(url, name));

	public void RaiseError(string message, Exception? error) => DiscoveryError?.Invoke(this, new DiscoveryErrorEventArgs(message, error));

	public void Dispose()
	{
	}
}
