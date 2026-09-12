using DisplayBook.App.Models;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MauiApplication = Microsoft.Maui.Controls.Application;
using NativeWindow = Microsoft.UI.Xaml.Window;

namespace DisplayBook.App.Services;

public sealed partial class BookPickerService
{
	public async partial Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress, CancellationToken cancellationToken)
	{
		Microsoft.Maui.Controls.Window? mauiWindow = MauiApplication.Current?.Windows[0];
		if (mauiWindow?.Handler?.PlatformView is not NativeWindow nativeWindow)
		{
			return null;
		}

		FolderPicker picker = new();
		picker.FileTypeFilter.Add("*");
		nint handle = WindowNative.GetWindowHandle(nativeWindow);
		InitializeWithWindow.Initialize(picker, handle);
		StorageFolder folder = await picker.PickSingleFolderAsync();
		cancellationToken.ThrowIfCancellationRequested();
		return folder?.Path;
	}
}