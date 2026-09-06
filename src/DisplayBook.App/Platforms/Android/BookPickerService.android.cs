using Android.Content;
using Android.Provider;
using DisplayBook.App.Models;
using Microsoft.Maui.ApplicationModel;

namespace DisplayBook.App.Services;

public sealed partial class BookPickerService
{
    public async partial Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress, CancellationToken cancellationToken)
    {
        if (Platform.CurrentActivity is not MainActivity activity)
        {
            return null;
        }

        var uriString = await activity.PickFolderUriAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(uriString))
        {
            return null;
        }

        var destination = Path.Combine(FileSystem.CacheDirectory, "DisplayBookFolderSelections", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try
        {
            var treeUri = Android.Net.Uri.Parse(uriString) ?? throw new IOException("Android returned an invalid folder URI.");
            var documentId = DocumentsContract.GetTreeDocumentId(treeUri) ?? throw new IOException("Android returned an invalid folder document.");
            await CopyDocumentTreeAsync(activity.ContentResolver!, treeUri, documentId, destination, progress, cancellationToken);
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
    }

    private static async Task CopyDocumentTreeAsync(
        Android.Content.ContentResolver resolver,
        Android.Net.Uri treeUri,
        string documentId,
        string destination,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, documentId)
            ?? throw new IOException("Android could not enumerate the selected folder.");
        var projection = new[] { "document_id", "_display_name", "mime_type" };
        using var cursor = resolver.Query(childrenUri, projection, null, null, null);
        if (cursor is null)
        {
            throw new IOException("Android could not read the selected folder.");
        }

        while (cursor.MoveToNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childId = cursor.GetString(cursor.GetColumnIndexOrThrow("document_id"));
            var displayName = cursor.GetString(cursor.GetColumnIndexOrThrow("_display_name"));
            var mimeType = cursor.GetString(cursor.GetColumnIndexOrThrow("mime_type"));
            if (string.IsNullOrWhiteSpace(childId) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(mimeType))
            {
                continue;
            }
            var safeName = Path.GetFileName(displayName);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                continue;
            }

            var childUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, childId)
                ?? throw new IOException("Android could not open an item in the selected folder.");
            var destinationPath = EpubPackageReader.ResolveWithinRoot(destination, safeName);
            if (string.Equals(mimeType, DocumentsContract.Document.MimeTypeDir, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(destinationPath);
                await CopyDocumentTreeAsync(resolver, treeUri, childId, destinationPath, progress, cancellationToken);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var input = resolver.OpenInputStream(childUri);
            if (input is null)
            {
                continue;
            }

            await using var output = File.Create(destinationPath);
            await input.CopyToAsync(output, cancellationToken);
            progress?.Report(new BookImportProgress("Copying selected folder", displayName, 0, 0));
        }
    }
}
