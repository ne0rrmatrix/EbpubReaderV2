namespace DisplayBook.App.Models;

public sealed record BookSummary(
    string Id,
    string Title,
    string Author,
    string Description,
    string Language,
    string Publisher,
    string CoverPath,
    string PublicationRoot,
    string PublicationOpfPath,
    string OriginalFileName,
    DateTimeOffset ImportedAt,
    DateTimeOffset? LastOpenedAt,
    string LocatorResourceHref,
    int LocatorPage,
    int LocatorPageCount,
    string ContentHash = "",
    string Isbn = "",
    string Asin = "");
