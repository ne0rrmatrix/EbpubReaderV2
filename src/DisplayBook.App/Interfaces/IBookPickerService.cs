using DisplayBook.App.Models;

namespace DisplayBook.App.Interfaces;

public interface IBookPickerService
{
	Task<FileResult?> PickEpubFileAsync(CancellationToken cancellationToken = default);
	Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default);
}
