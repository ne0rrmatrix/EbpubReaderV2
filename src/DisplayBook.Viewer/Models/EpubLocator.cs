namespace DisplayBook.Viewer.Models;

public sealed record EpubLocator(
    string ResourceHref,
    int Page,
    int PageCount)
{
    public static EpubLocator Empty { get; } = new(string.Empty, 0, 1);
}
