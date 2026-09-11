using DisplayBook.App.Models;
using DisplayBook.App.Services.Opds;
using Xunit;

namespace DisplayBook.Tests;

/// <summary>
/// Extra coverage for <see cref="DownloadFileNaming"/> beyond the happy-path and collision tests
/// that already exist: slug truncation, the "download" fallback title, and the extension
/// resolution fallback chain (MIME type, then URL extension, then format name, then ".bin").
/// </summary>
public sealed class DownloadFileNamingTests
{
	static DownloadLink Link(string url, string format, string? title = null) =>
		new() { Url = url, Format = format, Title = title };

	[Fact]
	public void GetFileName_VeryLongTitleTruncatesTheSlug()
	{
		const int MaxSlugLength = 72;
		string title = string.Concat(Enumerable.Repeat("ab", 60)); // 120 chars, all alphanumerics.

		string name = DownloadFileNaming.GetFileName(Link("http://calibre.local:8012/get/epub/1/Lib", "application/epub+zip"), title);

		// The slug sits before the first dash and must be bounded to the maximum length.
		string slug = name[..name.IndexOf('-')];
		Assert.Equal(MaxSlugLength, slug.Length);
		Assert.EndsWith(".epub", name);
	}

	[Fact]
	public void GetFileName_TitleWithoutLetters_FallsBackToDownload()
	{
		string name = DownloadFileNaming.GetFileName(Link("http://calibre.local:8012/get/epub/1/Lib", "application/epub+zip", "###"), "###");

		Assert.StartsWith("download-", name);
	}

	[Fact]
	public void GetFileName_EmptyBookTitleUsesLinkTitle()
	{
		string name = DownloadFileNaming.GetFileName(Link("http://calibre.local:8012/get/epub/1/Lib", "application/epub+zip", "Dune"), "   ");

		Assert.StartsWith("dune-", name);
	}

	[Theory]
	[InlineData("application/pdf", ".pdf")]
	[InlineData("application/x-mobipocket-ebook", ".mobi")]
	[InlineData("application/kindle+azw3", ".azw3")]
	[InlineData("application/rtf", ".rtf")]
	[InlineData("text/plain", ".txt")]
	public void GetFileName_KnownMimeMapsToExtension(string mime, string expected)
	{
		string name = DownloadFileNaming.GetFileName(Link("http://calibre.local:8012/get/1", mime), "Book");

		Assert.EndsWith(expected, name);
	}

	[Fact]
	public void GetFileName_UnknownMimeUsesUrlExtension()
	{
		string name = DownloadFileNaming.GetFileName(Link("http://calibre.local:8012/files/Story.epub", "application/octet-stream"), "Story");

		Assert.EndsWith(".epub", name);
	}

	[Fact]
	public void GetFileName_UnknownMimeAndNoUrlExtensionFallsBackToBin()
	{
		string name = DownloadFileNaming.GetFileName(Link("http://calibre.local:8012/get/1", "application/unknown-type"), "Book");

		Assert.EndsWith(".bin", name);
	}

	[Fact]
	public void GetFileName_SameUrlAndTitleIsStable()
	{
		string first = DownloadFileNaming.GetFileName(Link("http://calibre.local:8012/get/epub/9/Lib", "application/epub+zip"), "Same");
		string second = DownloadFileNaming.GetFileName(Link("http://calibre.local:8012/get/epub/9/Lib", "application/epub+zip"), "Same");

		Assert.Equal(first, second);
	}
}
