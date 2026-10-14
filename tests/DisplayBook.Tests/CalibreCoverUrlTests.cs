using DisplayBook.App.Services;
using Xunit;

namespace DisplayBook.Tests;

public class CalibreCoverUrlTests
{
	[Theory]
	[InlineData(null, "")]
	[InlineData("", "")]
	[InlineData("   ", "   ")]
	public void Upgrade_NullOrWhitespace_PassesThrough(string? input, string expected)
	{
		Assert.Equal(expected, CalibreCoverUrl.Upgrade(input));
	}

	[Fact]
	public void Upgrade_ThumbUrlWithSizeQuery_RewritesToFullCoverWithoutQuery()
	{
		Assert.Equal(
			"http://calibre.local:8080/get/cover/3819/Calibre_Library",
			CalibreCoverUrl.Upgrade("http://calibre.local:8080/get/thumb/3819/Calibre_Library?sz=600x800"));
	}

	[Fact]
	public void Upgrade_ThumbUrlWithoutQuery_RewritesToFullCover()
	{
		Assert.Equal(
			"http://calibre.local:8080/get/cover/3819/Calibre_Library",
			CalibreCoverUrl.Upgrade("http://calibre.local:8080/get/thumb/3819/Calibre_Library"));
	}

	[Fact]
	public void Upgrade_ThumbSegmentMatchIsCaseInsensitive()
	{
		Assert.Equal(
			"http://calibre.local:8080/get/cover/42/Library",
			CalibreCoverUrl.Upgrade("http://calibre.local:8080/GET/THUMB/42/Library?sz=300x400"));
	}

	[Fact]
	public void Upgrade_ThumbUnderUrlPrefix_KeepsPrefix()
	{
		Assert.Equal(
			"https://example.com/calibre/get/cover/7/Books",
			CalibreCoverUrl.Upgrade("https://example.com/calibre/get/thumb/7/Books?sz=100x100"));
	}

	[Theory]
	[InlineData("http://calibre.local:8080/get/cover/3819/Calibre_Library")]
	[InlineData("https://covers.openlibrary.org/b/id/12345-L.jpg")]
	[InlineData("http://calibre.local:8080/thumbnail/3819?sz=600x800")]
	public void Upgrade_NonThumbUrl_PassesThroughUnchanged(string url)
	{
		Assert.Equal(url, CalibreCoverUrl.Upgrade(url));
	}
}
