using DisplayBook.App.Models;
using Xunit;

namespace DisplayBook.Tests;

/// <summary>
/// Model-helper tests for <see cref="OpdsFeed"/> link lookups and the remaining
/// <see cref="Link"/> classification edges (blank relation, acquisition relations) that the
/// parser-level tests do not exercise directly.
/// </summary>
public class OpdsFeedHelperTests
{
    private static Link MakeLink(string? rel, string href = "http://calibre.local:8080/x") => new() { Rel = rel, Href = href };

    [Fact]
    public void GetLinks_ReturnsTheMatchingRelation()
    {
        var feed = new OpdsFeed
        {
            Links =
            [
                MakeLink(Link.RelSelf, "http://calibre.local:8080/"),
                MakeLink(Link.RelPrev, "http://calibre.local:8080/?page=0"),
                MakeLink(Link.RelNext, "http://calibre.local:8080/?page=2"),
                MakeLink(Link.RelUp, "http://calibre.local:8080/../"),
                MakeLink(Link.RelSearch, "http://calibre.local:8080/search"),
                MakeLink(Link.RelFirst, "http://calibre.local:8080/?page=0"),
                MakeLink(Link.RelLast, "http://calibre.local:8080/?page=9"),
                MakeLink("http://opds-spec.org/acquisition", "http://calibre.local:8080/book")
            ]
        };

        Assert.Equal("http://calibre.local:8080/", feed.GetSelfLink()?.Href);
        Assert.Equal("http://calibre.local:8080/?page=2", feed.GetNextLink()?.Href);
        Assert.Equal("http://calibre.local:8080/?page=0", feed.GetPrevLink()?.Href);
        Assert.Equal("http://calibre.local:8080/../", feed.GetUpLink()?.Href);
        Assert.Equal("http://calibre.local:8080/search", feed.GetSearchLink()?.Href);
        Assert.Equal("http://calibre.local:8080/?page=0", feed.GetFirstLink()?.Href);
        Assert.Equal("http://calibre.local:8080/?page=9", feed.GetLastLink()?.Href);
    }

    [Fact]
    public void GetLinks_MissingRelation_ReturnsNull()
    {
        var feed = new OpdsFeed { Links = [MakeLink(Link.RelSelf)] };

        Assert.Equal("http://calibre.local:8080/x", feed.GetSelfLink()?.Href);
        Assert.Null(feed.GetNextLink());
        Assert.Null(feed.GetPrevLink());
        Assert.Null(feed.GetUpLink());
        Assert.Null(feed.GetSearchLink());
        Assert.Null(feed.GetFirstLink());
        Assert.Null(feed.GetLastLink());
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void Link_BlankRelation_IsAcquisitionAndNotNavigation(string? rel)
    {
        var link = new Link { Rel = rel };

        Assert.True(link.IsAcquisition());
        Assert.False(link.IsNavigation());
    }

    [Theory]
    [InlineData("http://opds-spec.org/acquisition", true)]
    [InlineData("http://opds-spec.org/sale", true)]
    [InlineData("http://opds-spec.org/sample", true)]
    [InlineData("self", true)]
    [InlineData("next", false)]
    [InlineData("search", false)]
    [InlineData("up", false)]
    public void Link_RelationClassification(string rel, bool expectedAcquisition)
    {
        var link = new Link { Rel = rel };

        Assert.Equal(expectedAcquisition, link.IsAcquisition());
        Assert.Equal(!expectedAcquisition, link.IsNavigation());
    }
}
