namespace DisplayBook.App.Models.BookMetadata;

public enum MetadataUpdatePanelState
{
	Idle,
	Loading,
	Found,
	RateLimited,
	NotFound,
	Applied,
}
