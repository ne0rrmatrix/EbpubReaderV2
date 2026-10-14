using System.Text;
using DisplayBook.Viewer.Services;
using Xunit;

namespace DisplayBook.Tests;

public class EpubArchiveTests
{
	static Task<EpubArchive> BuildAsync() => TestEpubFileBuilder.BuildAsync(new Dictionary<string, string>
	{
		["mimetype"] = "application/epub+zip",
		["OEBPS/"] = string.Empty,
		["OEBPS/Text/chapter one.xhtml"] = "<html>one</html>",
		["OEBPS/Styles/main.css"] = "body{}",
	});

	static string GetText(EpubArchive archive, string requestPath)
	{
		Assert.True(archive.TryGetEntry(requestPath, out byte[] bytes), $"Expected an entry for '{requestPath}'.");
		return Encoding.UTF8.GetString(bytes);
	}

	[Fact]
	public async Task TryGetEntry_ExactPath_ReturnsEntryBytes()
	{
		EpubArchive archive = await BuildAsync();

		Assert.Equal("body{}", GetText(archive, "OEBPS/Styles/main.css"));
	}

	[Fact]
	public async Task TryGetEntry_IsCaseInsensitive()
	{
		EpubArchive archive = await BuildAsync();

		Assert.Equal("body{}", GetText(archive, "oebps/styles/MAIN.CSS"));
	}

	[Theory]
	[InlineData("/OEBPS/Styles/main.css")]
	[InlineData("OEBPS/Styles/main.css?v=2")]
	[InlineData("OEBPS/Styles/main.css#top")]
	[InlineData("OEBPS\\Styles\\main.css")]
	public async Task TryGetEntry_NormalizesRequestPath(string requestPath)
	{
		EpubArchive archive = await BuildAsync();

		Assert.Equal("body{}", GetText(archive, requestPath));
	}

	[Fact]
	public async Task TryGetEntry_UnescapesPercentEncodedPath()
	{
		EpubArchive archive = await BuildAsync();

		Assert.Equal("<html>one</html>", GetText(archive, "OEBPS/Text/chapter%20one.xhtml"));
	}

	[Theory]
	[InlineData("OEBPS/")]
	[InlineData("OEBPS/Text/missing.xhtml")]
	public async Task TryGetEntry_DirectoryOrMissingEntry_ReturnsFalse(string requestPath)
	{
		EpubArchive archive = await BuildAsync();

		Assert.False(archive.TryGetEntry(requestPath, out _));
	}

	[Fact]
	public async Task SetSyntheticEntry_IsServedLikeARealEntry()
	{
		EpubArchive archive = await BuildAsync();

		archive.SetSyntheticEntry("/__displaybook/combined.xhtml", Encoding.UTF8.GetBytes("combined"));

		Assert.Equal("combined", GetText(archive, "__displaybook/combined.xhtml?cache=1"));
	}

	[Fact]
	public async Task SetSyntheticEntry_ReplacesExistingEntry()
	{
		EpubArchive archive = await BuildAsync();

		archive.SetSyntheticEntry("OEBPS/Styles/main.css", Encoding.UTF8.GetBytes("replaced"));

		Assert.Equal("replaced", GetText(archive, "OEBPS/Styles/main.css"));
	}

	[Fact]
	public void NormalizeRequestPath_MalformedEscape_LeavesPathAsIs()
	{
		Assert.Equal("OEBPS/100%.css", EpubArchive.NormalizeRequestPath("/OEBPS/100%.css"));
	}
}
