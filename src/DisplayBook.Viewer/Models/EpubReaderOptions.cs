namespace DisplayBook.Viewer.Models;

public sealed record EpubReaderOptions
{
	public bool ShowWebReaderChrome { get; init; }

	public bool EnableTapNavigation { get; init; } = true;
}
