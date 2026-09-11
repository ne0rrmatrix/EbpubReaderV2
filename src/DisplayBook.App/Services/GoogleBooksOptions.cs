namespace DisplayBook.App.Services;

/// <summary>
/// Resolves the Google Books API key, checked in this order:
/// 1. <c>DISPLAYBOOK_GOOGLE_BOOKS_API_KEY</c> environment variable — desktop/dev only;
///    env vars do NOT cross the host/device boundary, so this is invisible to an
///    Android/iOS build even when set on the machine running the emulator.
/// 2. A device-level <see cref="Preferences"/> entry, so a future Settings page can
///    set it without a code change.
/// 3. A local, gitignored <c>Resources/Raw/googlebooks_apikey.txt</c> file bundled as
///    a MauiAsset — this is the one path that actually reaches every platform
///    (Windows, Android, iOS, MacCatalyst) from a single local dev setup, since the
///    key gets packaged into the app itself rather than read from the host OS.
/// Unset is a valid state at every step — Google Books still answers keyless
/// requests, just more likely to be throttled (in practice, as of 2026, keyless
/// traffic has been observed hard-throttled to a 0/day quota bucket).
/// </summary>
public static class GoogleBooksOptions
{
	const string environmentVariableName = "DISPLAYBOOK_GOOGLE_BOOKS_API_KEY";
	const string preferenceKey = "GoogleBooksApiKey";
	const string bundledKeyAssetName = "googlebooks_apikey.txt";

	static string? cachedBundledKey;
	static bool bundledKeyLoaded;

	public static async Task<string?> GetApiKeyAsync()
	{
		string? fromEnvironment = Environment.GetEnvironmentVariable(environmentVariableName);
		if (!string.IsNullOrWhiteSpace(fromEnvironment))
		{
			return fromEnvironment;
		}

		string? fromPreferences = Preferences.Default.Get(preferenceKey, (string?)null);
		return !string.IsNullOrWhiteSpace(fromPreferences) ? fromPreferences : await GetBundledKeyAsync();
	}

	static async Task<string?> GetBundledKeyAsync()
	{
		if (bundledKeyLoaded)
		{
			return cachedBundledKey;
		}

		try
		{
			await using Stream stream = await FileSystem.OpenAppPackageFileAsync(bundledKeyAssetName);
			using StreamReader reader = new(stream);
			string content = (await reader.ReadToEndAsync()).Trim();
			cachedBundledKey = content.Length == 0 ? null : content;
		}
		catch (Exception)
		{
			// Missing asset (no local key configured) or a platform read failure —
			// either way, degrade to keyless rather than fail the metadata lookup.
			cachedBundledKey = null;
		}

		bundledKeyLoaded = true;
		return cachedBundledKey;
	}
}
