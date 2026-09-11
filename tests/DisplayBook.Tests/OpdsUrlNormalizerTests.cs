using DisplayBook.App.Services;
using Xunit;

namespace DisplayBook.Tests;

public class OpdsUrlNormalizerTests
{
	[Theory]
	[InlineData(null, "")]
	[InlineData("   ", "")]
	[InlineData("", "")]
	public void Normalize_NullOrWhitespace_ReturnsEmpty(string? input, string expected)
	{
		Assert.Equal(expected, OpdsUrlNormalizer.Normalize(input));
	}

	[Fact]
	public void Normalize_TrailingSlash_IsRemoved()
	{
		Assert.Equal("http://localhost:8080/opds", OpdsUrlNormalizer.Normalize("http://localhost:8080/opds/"));
	}

	[Fact]
	public void Normalize_MultipleTrailingSlashes_AreRemoved()
	{
		Assert.Equal("http://host.example:80/opds", OpdsUrlNormalizer.Normalize("http://host.example/opds///"));
	}

	[Fact]
	public void Normalize_QueryString_IsIgnored()
	{
		Assert.Equal("http://host.example:80/opds",
			OpdsUrlNormalizer.Normalize("http://host.example/opds?search=star&lang=eng"));
	}

	[Fact]
	public void Normalize_Fragment_IsIgnored()
	{
		Assert.Equal("http://host.example:80/opds",
			OpdsUrlNormalizer.Normalize("http://host.example/opds#section"));
	}

	[Fact]
	public void Normalize_TrimsSurroundingWhitespace()
	{
		Assert.Equal("http://host.example:80/opds",
			OpdsUrlNormalizer.Normalize("  http://host.example/opds/  "));
	}

	[Fact]
	public void Normalize_EquivalentUrls_AreIdentical()
	{
		string a = OpdsUrlNormalizer.Normalize("http://host.example:8080/opds/");
		string b = OpdsUrlNormalizer.Normalize("http://host.example:8080/opds?feed=1");
		Assert.Equal(a, b);
	}

	[Fact]
	public void Normalize_ExplicitPort_IsPreserved()
	{
		Assert.Equal("http://host.example:8080/opds",
			OpdsUrlNormalizer.Normalize("http://host.example:8080/opds/"));
	}

	[Fact]
	public void Normalize_DefaultPort_IsRenderedExplicitly()
	{
		// The canonical form always includes the port, even for the scheme default.
		Assert.Equal("http://host.example:80/opds", OpdsUrlNormalizer.Normalize("http://host.example/opds"));
		Assert.Equal("https://host.example:443/opds", OpdsUrlNormalizer.Normalize("https://host.example/opds"));
	}

	[Fact]
	public void Normalize_DifferentPorts_AreDifferent()
	{
		Assert.NotEqual(
			OpdsUrlNormalizer.Normalize("http://host.example:8080/opds"),
			OpdsUrlNormalizer.Normalize("http://host.example:8081/opds"));
	}

	[Fact]
	public void Normalize_SchemeCase_IsCanonicalized()
	{
		Assert.Equal("https://host.example:443/opds",
			OpdsUrlNormalizer.Normalize("HTTPS://host.example/opds/"));
	}

	[Fact]
	public void Normalize_NonHttpUrl_FallsBackToTrimOnly()
	{
		Assert.Equal("ftp://host.example/opds",
			OpdsUrlNormalizer.Normalize("ftp://host.example/opds///"));
	}

	[Fact]
	public void Normalize_RelativePath_IsTrimmedOnly()
	{
		Assert.Equal("opds/catalog", OpdsUrlNormalizer.Normalize("opds/catalog/"));
	}
}
