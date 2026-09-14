using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Opds;
using Xunit;

namespace DisplayBook.Tests;

public sealed class OpdsDownloadAndMembershipTests
{
	[Fact]
	public void GetFileName_UsesReadableTitleUrlHashAndEpubExtension()
	{
		DownloadLink link = new()
		{
			Url = "http://calibre.local:8012/get/epub/3819/Calibre_Library",
			Format = "application/epub+zip"
		};

		string fileName = DownloadFileNaming.GetFileName(link, "Elric: The Stealer of Souls");

		Assert.StartsWith("elric-the-stealer-of-souls-", fileName);
		Assert.EndsWith(".epub", fileName);
		Assert.DoesNotContain("/", fileName);
		Assert.DoesNotContain("\\", fileName);
	}

	[Fact]
	public void GetFileName_DifferentAcquisitionUrlsDoNotCollideAtSharedCalibrePath()
	{
		DownloadLink first = new()
		{
			Url = "http://calibre.local:8012/get/epub/3819/Calibre_Library",
			Format = "application/epub+zip"
		};
		DownloadLink second = new()
		{
			Url = "http://calibre.local:8012/get/epub/3820/Calibre_Library",
			Format = "application/epub+zip"
		};

		string firstName = DownloadFileNaming.GetFileName(first, "First book");
		string secondName = DownloadFileNaming.GetFileName(second, "Second book");

		Assert.NotEqual(firstName, secondName);
	}

	[Fact]
	public void BookMetadataMatcher_IgnoresCaseWhitespaceAndPunctuation()
	{
		Assert.True(BookMetadataMatcher.Matches(
			"The Hobbit",
			"J.R.R. Tolkien",
			"  the   hobbit ",
			"jrr tolkien"));
	}

	[Fact]
	public void BookMetadataMatcher_RequiresMatchingTitleAndAuthor()
	{
		Assert.False(BookMetadataMatcher.Matches("The Hobbit", "J.R.R. Tolkien", "The Hobbit", "Ursula Le Guin"));
		Assert.False(BookMetadataMatcher.Matches("The Hobbit", "", "The Hobbit", ""));
	}
}