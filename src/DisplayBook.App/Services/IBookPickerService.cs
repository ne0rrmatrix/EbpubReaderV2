using DisplayBook.App.Models;
using Microsoft.Maui.Storage;

namespace DisplayBook.App.Services;

public interface IBookPickerService
{
    Task<FileResult?> PickEpubFileAsync(CancellationToken cancellationToken = default);
    Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default);
}
