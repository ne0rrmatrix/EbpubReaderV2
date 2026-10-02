using DisplayBook.App.Models;

namespace DisplayBook.App.Interfaces;

public interface IBookImportService
{
	Task<IReadOnlyList<BookSummary>> ImportFileAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default);
	Task<IReadOnlyList<BookSummary>> ImportLocalFileAsync(string filePath, IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default);
	Task<IReadOnlyList<BookSummary>> ImportFolderAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default);
}