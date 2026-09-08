using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Xunit;

namespace DisplayBook.Tests;

/// <summary>
/// Tests for <see cref="BookMetadataMatcher"/>. The URL-normalizer cases and a couple of the
/// basic title/author comparisons are already covered elsewhere in this suite; this file focuses
/// on the text-normalization edge cases (diacritics, case folding, punctuation) and on the
/// <see cref="BookSummary"/> overload of the matcher.
/// </summary>
public class BookMetadataMatcherTests
{
    [Theory]
    [InlineData("café", "cafe")]
    [InlineData("Crème Brûlée", "cremebrulee")]
    [InlineData("Ünïcödé", "unicode")]
    [InlineData("naïve résumé", "naiveresume")]
    public void Normalize_StripsNonSpacingDiacritics(string input, string expected)
    {
        Assert.Equal(expected, BookMetadataMatcher.Normalize(input));
    }

    [Fact]
    public void Normalize_FoldsCaseToInvariantLower()
    {
        Assert.Equal("abc123", BookMetadataMatcher.Normalize("ABC123"));
    }

    [Fact]
    public void Normalize_DropsWhitespaceAndPunctuation()
    {
        Assert.Equal("thehobbit", BookMetadataMatcher.Normalize("The  Hobbit!"));
        Assert.Equal("jrrtolkien", BookMetadataMatcher.Normalize("J.R.R. Tolkien"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    public void Normalize_NullOrWhitespaceIsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, BookMetadataMatcher.Normalize(input));
    }

    [Fact]
    public void Matches_OverBookSummary_ComparesAgainstItsTitleAndAuthor()
    {
        var book = Book("1", "The Hobbit", "J.R.R. Tolkien");

        Assert.True(BookMetadataMatcher.Matches(book, "the hobbit", "jrr tolkien"));
        Assert.False(BookMetadataMatcher.Matches(book, "the hobbit", "Ursula Le Guin"));
    }

    [Theory]
    [InlineData("", null, "", null, false)]
    [InlineData("The Hobbit", "", "The Hobbit", "Tolkien", false)]
    [InlineData("   ", "  ", "x", "y", false)]
    [InlineData(null, null, "anything", "else", false)]
    public void Matches_EitherSideMissingTitleOrAuthor_ReturnsFalse(
        string? libraryTitle, string? libraryAuthor, string? title, string? author, bool expected)
    {
        Assert.Equal(expected, BookMetadataMatcher.Matches(libraryTitle, libraryAuthor, title, author));
    }

    [Fact]
    public void Matches_NullBook_IsArgumentError()
    {
        Assert.Throws<ArgumentNullException>(() => BookMetadataMatcher.Matches(null!, "t", "a"));
    }

    private static BookSummary Book(string id, string title, string author) =>
        new BookSummary(
            Id: id,
            Title: title,
            Author: author,
            Description: "",
            Language: "en",
            Publisher: "",
            CoverPath: "",
            PublicationRoot: "",
            PublicationOpfPath: "",
            OriginalFileName: "",
            ImportedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastOpenedAt: null,
            LocatorResourceHref: "OEBPS/ch978.xhtml",
            LocatorPage: 12,
            LocatorPageCount: 0);
}
