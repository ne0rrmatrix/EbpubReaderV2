using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public sealed partial class BookPickerService : IBookPickerService
{
	public async Task<FileResult?> PickEpubFileAsync(CancellationToken cancellationToken = default)
	{
		FileResult? result = await FilePicker.Default.PickAsync(new PickOptions
		{
			PickerTitle = "Choose an EPUB book"
		});
		cancellationToken.ThrowIfCancellationRequested();
		return result;
	}

	public partial Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default);
}
