using DisplayBook.App.Models;

namespace DisplayBook.App.Interfaces;

public interface IBookPickerService
{
	Task<FileResult?> PickEpubFileAsync(CancellationToken cancellationToken = default);
	// No default argument values: the implementation is a partial method whose platform-specific
	// implementing declarations cannot restate them (CS1066), so declaring them here would leave
	// the interface and the implementation disagreeing.
	Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress, CancellationToken cancellationToken);
}