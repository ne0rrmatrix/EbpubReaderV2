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
    private const string EnvironmentVariableName = "DISPLAYBOOK_GOOGLE_BOOKS_API_KEY";
    private const string PreferenceKey = "GoogleBooksApiKey";
    private const string BundledKeyAssetName = "googlebooks_apikey.txt";

    private static string? _cachedBundledKey;
    private static bool _bundledKeyLoaded;

    public static async Task<string?> GetApiKeyAsync()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var fromPreferences = Preferences.Default.Get(PreferenceKey, (string?)null);
        if (!string.IsNullOrWhiteSpace(fromPreferences))
        {
            return fromPreferences;
        }

        return await GetBundledKeyAsync();
    }

    private static async Task<string?> GetBundledKeyAsync()
    {
        if (_bundledKeyLoaded)
        {
            return _cachedBundledKey;
        }

        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync(BundledKeyAssetName);
            using var reader = new StreamReader(stream);
            var content = (await reader.ReadToEndAsync()).Trim();
            _cachedBundledKey = content.Length == 0 ? null : content;
        }
        catch (Exception)
        {
            // Missing asset (no local key configured) or a platform read failure —
            // either way, degrade to keyless rather than fail the metadata lookup.
            _cachedBundledKey = null;
        }

        _bundledKeyLoaded = true;
        return _cachedBundledKey;
    }
}
