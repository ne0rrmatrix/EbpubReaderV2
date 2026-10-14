using DisplayBook.App.Interfaces;
using DisplayBook.App.Services;
using Microsoft.Data.Sqlite;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// Every test that touches <see cref="FileSystem.AppDataDirectory"/>, <see cref="SecureStorage"/>,
/// <see cref="Preferences"/> or the app's DI singletons runs in this collection: those are
/// process-wide, so tests that share them must not run in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppDataCollection
{
	public const string Name = "App data";
}

/// <summary>
/// Puts the device-test app's own data back to a clean state. The services under test resolve
/// their paths from <see cref="BookStorageService"/>, which this app shares only by name with
/// DisplayBook.App: both are scoped to the running app's package, and this app's ApplicationId
/// differs. <see cref="EnsureTestApp"/> checks that before anything is deleted, so these tests
/// can never wipe a real library.
/// </summary>
static class AppDataSandbox
{
	/// <summary>
	/// Clears the library, OPDS data, downloads, stored sign-in and every test fake. The app's DI
	/// singletons hold state of their own (the server list, the download queue, the signed-in
	/// user, and the database's one-time schema setup), so they are cleared through their own
	/// APIs, and the database schema is recreated unless <paramref name="primeDatabase"/> is false.
	/// </summary>
	public static async Task ResetAsync(bool primeDatabase = true)
	{
		EnsureTestApp();
		IServiceProvider services = TestApp.Services;

		IOpdsServerRepository servers = services.GetRequiredService<IOpdsServerRepository>();
		foreach (App.Models.OpdsServer server in await servers.GetAllAsync())
		{
			await servers.RemoveAsync(server.Id);
		}

		IDownloadQueueService queue = services.GetRequiredService<IDownloadQueueService>();
		await queue.CancelAllAsync();
		await queue.ClearFinishedAsync();

		await services.GetRequiredService<IFirebaseAuthService>().SignOutAsync();
		SecureStorage.Default.RemoveAll();

		// Microsoft.Data.Sqlite pools connections, and a pooled connection keeps the database
		// file open, which stops Windows from deleting it. A download cancelled just above may
		// still be closing its file, hence the retries.
		SqliteConnection.ClearAllPools();
		await RetryAsync(() =>
		{
			DeleteFileIfExists(BookStorageService.DatabasePath);
			DeleteDirectoryIfExists(BookStorageService.ContentRoot);
			DeleteDirectoryIfExists(Path.Combine(FileSystem.CacheDirectory, "DisplayBookImports"));
		});

		if (primeDatabase)
		{
			using BookDatabase database = new();
			await database.GetBooksAsync();
			SqliteConnection.ClearAllPools();

			// The download queue singleton creates this folder once, on its first download, and
			// relies on it from then on; in the app nothing deletes it, but the reset above does.
			Directory.CreateDirectory(Path.Combine(BookStorageService.ContentRoot, "Opds", "Downloads"));
		}

		TestDialogs.Reset();
		FakeNetwork.Reset();
		FakeBookPicker.Reset();
		FakeBonjourDiscovery.StartFailure = null;
	}

	static void EnsureTestApp()
	{
		string packageName = AppInfo.Current.PackageName;
		if (!packageName.Contains("devicetests", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException(
				$"Refusing to reset app data: this process is '{packageName}', not the device-test app. " +
				"These tests delete the app's library, database and stored sign-in.");
		}
	}

	static async Task RetryAsync(Action action)
	{
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				action();
				return;
			}
			catch (IOException) when (attempt < 20)
			{
				await Task.Delay(100);
			}
			catch (UnauthorizedAccessException) when (attempt < 20)
			{
				await Task.Delay(100);
			}
		}
	}

	static void DeleteFileIfExists(string path)
	{
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	static void DeleteDirectoryIfExists(string path)
	{
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
	}
}

/// <summary>Base for test classes in <see cref="AppDataCollection"/>: each test starts from a clean sandbox.</summary>
[Collection(AppDataCollection.Name)]
public abstract class AppDataTest : IAsyncLifetime
{
	public virtual Task InitializeAsync() => AppDataSandbox.ResetAsync();

	public virtual Task DisposeAsync() => Task.CompletedTask;
}
