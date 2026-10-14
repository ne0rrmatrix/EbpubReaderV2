using DisplayBook.Viewer.Services;
using Xunit;

namespace DisplayBook.Tests;

public class EpubPathUtilitiesTests
{
	[Theory]
	[InlineData("OEBPS/content.opf", "OEBPS/content.opf")]
	[InlineData("/OEBPS/content.opf", "OEBPS/content.opf")]
	[InlineData("OEBPS\\Text\\ch1.xhtml", "OEBPS/Text/ch1.xhtml")]
	[InlineData("\\OEBPS\\ch1.xhtml", "OEBPS/ch1.xhtml")]
	public void NormalizeEntryPath_UsesForwardSlashesWithoutLeadingSlash(string input, string expected)
	{
		Assert.Equal(expected, EpubPathUtilities.NormalizeEntryPath(input));
	}

	[Theory]
	[InlineData("Books/abc.epub", "../Books/abc.epub")]
	[InlineData("/Books/abc.epub/", "../Books/abc.epub")]
	[InlineData("Books\\abc.epub", "../Books/abc.epub")]
	public void GetBookRelativePath_EscapesOutOfViewerFolder(string input, string expected)
	{
		Assert.Equal(expected, EpubPathUtilities.GetBookRelativePath(input));
	}

	[Theory]
	[InlineData("OEBPS/Text/ch1.xhtml", "OEBPS/Text")]
	[InlineData("OEBPS/content.opf", "OEBPS")]
	[InlineData("content.opf", "")]
	public void GetEntryDirectory_ReturnsEverythingBeforeLastSlash(string input, string expected)
	{
		Assert.Equal(expected, EpubPathUtilities.GetEntryDirectory(input));
	}

	[Theory]
	[InlineData("OEBPS", "Text/ch1.xhtml", "OEBPS/Text/ch1.xhtml")]
	[InlineData("", "ch1.xhtml", "ch1.xhtml")]
	[InlineData("OEBPS/Text", "../Images/cover.jpg", "OEBPS/Images/cover.jpg")]
	[InlineData("OEBPS/Text", "./ch2.xhtml", "OEBPS/Text/ch2.xhtml")]
	[InlineData("OEBPS/Text", "../../root.css", "root.css")]
	public void CombinePath_ResolvesDotSegments(string baseDirectory, string relativePath, string expected)
	{
		Assert.Equal(expected, EpubPathUtilities.CombinePath(baseDirectory, relativePath));
	}

	[Fact]
	public void CombinePath_ParentSegmentsBeyondRoot_StopAtRoot()
	{
		Assert.Equal("style.css", EpubPathUtilities.CombinePath("OEBPS", "../../../style.css"));
	}

	[Theory]
	[InlineData("ch1.xhtml#section2")]
	[InlineData("ch1.xhtml?v=1")]
	[InlineData("ch1.xhtml?v=1#section2")]
	public void CombinePath_StripsQueryAndFragment(string relativePath)
	{
		Assert.Equal("OEBPS/ch1.xhtml", EpubPathUtilities.CombinePath("OEBPS", relativePath));
	}

	[Fact]
	public void CombinePath_UnescapesPercentEncodedSegments()
	{
		Assert.Equal("OEBPS/Text/chapter one.xhtml", EpubPathUtilities.CombinePath("OEBPS/Text", "chapter%20one.xhtml"));
	}

	[Fact]
	public void CombinePath_NormalizesBackslashesAndDoubleSlashes()
	{
		Assert.Equal("OEBPS/Images/cover.jpg", EpubPathUtilities.CombinePath("OEBPS", "Images\\\\cover.jpg"));
	}
}
