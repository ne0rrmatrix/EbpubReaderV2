using DisplayBook.App.Services.BookMetadata;
using Xunit;

namespace DisplayBook.Tests;

public class BookIdentifiersTests
{
	[Fact]
	public void Classify_ValidIsbn13_ReturnsIsbn13()
	{
		Assert.Equal(BookIdentifierKind.Isbn13, BookIdentifiers.Classify("9780132350884"));
	}

	[Fact]
	public void Classify_Isbn13WithBadChecksum_ReturnsUnknown()
	{
		Assert.Equal(BookIdentifierKind.Unknown, BookIdentifiers.Classify("9780132350885"));
	}

	[Fact]
	public void Classify_ValidIsbn10_ReturnsIsbn10()
	{
		Assert.Equal(BookIdentifierKind.Isbn10, BookIdentifiers.Classify("0132350882"));
	}

	[Fact]
	public void Classify_Isbn10WithBadChecksum_ReturnsUnknown()
	{
		Assert.Equal(BookIdentifierKind.Unknown, BookIdentifiers.Classify("0132350881"));
	}

	[Fact]
	public void Classify_NotAnIsbn_ReturnsUnknown()
	{
		Assert.Equal(BookIdentifierKind.Unknown, BookIdentifiers.Classify("not-an-isbn"));
	}

	[Fact]
	public void Isbn13ToIsbn10_ConvertsCorrectly()
	{
		Assert.Equal("0132350882", BookIdentifiers.Isbn13ToIsbn10("9780132350884"));
	}

	[Fact]
	public void Isbn13ToIsbn10_UnconvertiblePrefix_ReturnsNull()
	{
		Assert.Null(BookIdentifiers.Isbn13ToIsbn10("9750132350884"));
	}

	[Fact]
	public void Isbn13ToIsbn10_NotAnIsbn13_ReturnsNull()
	{
		Assert.Null(BookIdentifiers.Isbn13ToIsbn10("0132350882"));
	}

	[Fact]
	public void Clean_StripsHyphensAndIsbnLabel()
	{
		Assert.Equal("9780132350884", BookIdentifiers.Clean("ISBN 978-0-13-235088-4"));
	}

	[Theory]
	[InlineData(null, "")]
	[InlineData("   ", "")]
	public void Clean_NullOrWhitespace_IsEmpty(string? input, string expected)
	{
		Assert.Equal(expected, BookIdentifiers.Clean(input));
	}
}