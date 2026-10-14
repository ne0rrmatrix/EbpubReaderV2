using DisplayBook.App.Services;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests;

/// <summary>
/// The key lookup order: environment variable, then the <see cref="Preferences"/> entry, then the
/// bundled asset. This app bundles no key asset, so that last step always finds nothing.
/// </summary>
public sealed partial class GoogleBooksOptionsTests : AppDataTest, IDisposable
{
	const string environmentVariable = "DISPLAYBOOK_GOOGLE_BOOKS_API_KEY";
	const string preferenceKey = "GoogleBooksApiKey";

	readonly string? originalEnvironmentValue = Environment.GetEnvironmentVariable(environmentVariable);

	public override async Task InitializeAsync()
	{
		await base.InitializeAsync();
		Environment.SetEnvironmentVariable(environmentVariable, null);
		Preferences.Default.Remove(preferenceKey);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable(environmentVariable, originalEnvironmentValue);
		Preferences.Default.Remove(preferenceKey);
	}

	[Fact]
	public async Task NothingConfigured_ReturnsNull()
	{
		Assert.Null(await GoogleBooksOptions.GetApiKeyAsync());
	}

	[Fact]
	public async Task Preference_IsUsedWhenNoEnvironmentVariable()
	{
		Preferences.Default.Set(preferenceKey, "from-preferences");

		Assert.Equal("from-preferences", await GoogleBooksOptions.GetApiKeyAsync());
	}

	[Fact]
	public async Task EnvironmentVariable_WinsOverPreference()
	{
		Preferences.Default.Set(preferenceKey, "from-preferences");
		Environment.SetEnvironmentVariable(environmentVariable, "from-environment");

		Assert.Equal("from-environment", await GoogleBooksOptions.GetApiKeyAsync());
	}

	[Fact]
	public async Task BlankPreference_IsIgnored()
	{
		Preferences.Default.Set(preferenceKey, "   ");

		Assert.Null(await GoogleBooksOptions.GetApiKeyAsync());
	}
}
