using DisplayBook.App.Models;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;
using MauiApplication = Microsoft.Maui.Controls.Application;
using NativeWindow = Microsoft.UI.Xaml.Window;

namespace DisplayBook.App.Services;

public sealed partial class BookPickerService
{
    public async partial Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress, CancellationToken cancellationToken)
    {
        var mauiWindow = MauiApplication.Current?.Windows.FirstOrDefault();
        if (mauiWindow?.Handler?.PlatformView is not NativeWindow nativeWindow)
        {
            return null;
        }

        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var handle = WindowNative.GetWindowHandle(nativeWindow);
        InitializeWithWindow.Initialize(picker, handle);
        var folder = await picker.PickSingleFolderAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return folder?.Path;
    }
}
