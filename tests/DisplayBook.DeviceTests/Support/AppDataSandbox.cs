using DisplayBook.App.Services;
using Microsoft.Data.Sqlite;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// Every test that touches <see cref="FileSystem.AppDataDirectory"/>, <see cref="SecureStorage"/>
/// or <see cref="Preferences"/> runs in this collection: those are process-wide, so tests that
/// share them must not run in parallel.
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
	public static void Reset()
	{
		EnsureTestApp();

		// Microsoft.Data.Sqlite pools connections, and a pooled connection keeps the database
		// file open, which stops Windows from deleting it.
		SqliteConnection.ClearAllPools();
		DeleteFileIfExists(BookStorageService.DatabasePath);
		if (Directory.Exists(BookStorageService.ContentRoot))
		{
			Directory.Delete(BookStorageService.ContentRoot, recursive: true);
		}

		SecureStorage.Default.RemoveAll();
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

	static void DeleteFileIfExists(string path)
	{
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}
}
