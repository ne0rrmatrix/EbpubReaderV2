using DisplayBook.App.Models;
using Foundation;
using Microsoft.Maui.ApplicationModel;
using UIKit;
using UniformTypeIdentifiers;

namespace DisplayBook.App.Services;

public sealed partial class BookPickerService
{
    public async partial Task<string?> PickFolderAsync(
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var presenter = Platform.GetCurrentUIViewController()
            ?? throw new InvalidOperationException("The MacCatalyst folder picker has no presenting view controller.");
        var picker = new UIDocumentPickerViewController(
            new[] { UTType.CreateFromIdentifier("public.folder") ?? throw new InvalidOperationException("Apple could not create the folder type.") },
            asCopy: true);
        var pickerDelegate = new FolderPickerDelegate();
        picker.Delegate = pickerDelegate;

        using var cancellationRegistration = cancellationToken.Register(() => pickerDelegate.Cancel(cancellationToken));
        await MainThread.InvokeOnMainThreadAsync(() => presenter.PresentViewController(picker, true, null));

        var selectedUrl = await pickerDelegate.Result.Task;
        if (selectedUrl is null)
        {
            return null;
        }

        var sourceRoot = selectedUrl.Path;
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            throw new IOException("MacCatalyst returned an invalid folder path.");
        }

        var hasSecurityScope = selectedUrl.StartAccessingSecurityScopedResource();
        var destination = Path.Combine(
            FileSystem.CacheDirectory,
            "DisplayBookFolderSelections",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);

        try
        {
            var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToArray();
            for (var index = 0; index < files.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = files[index];
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                var destinationPath = EpubPackageReader.ResolveWithinRoot(destination, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                await using var input = File.OpenRead(sourcePath);
                await using var output = File.Create(destinationPath);
                await input.CopyToAsync(output, cancellationToken);
                progress?.Report(new BookImportProgress(
                    "Copying selected folder",
                    relativePath,
                    index + 1,
                    files.Length));
            }

            return destination;
        }
        catch
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            throw;
        }
        finally
        {
            if (hasSecurityScope)
            {
                selectedUrl.StopAccessingSecurityScopedResource();
            }
        }
    }

    private sealed class FolderPickerDelegate : UIDocumentPickerDelegate
    {
        internal TaskCompletionSource<NSUrl?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl url)
        {
            controller.DismissViewController(true, null);
            Result.TrySetResult(url);
        }

        public override void WasCancelled(UIDocumentPickerViewController controller)
        {
            controller.DismissViewController(true, null);
            Result.TrySetResult(null);
        }

        internal void Cancel(CancellationToken cancellationToken)
        {
            Result.TrySetCanceled(cancellationToken);
        }
    }
}
