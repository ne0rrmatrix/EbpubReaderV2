using Android.Database;
using Android.Provider;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public sealed partial class BookPickerService
{
	public async partial Task<string?> PickFolderAsync(IProgress<BookImportProgress>? progress, CancellationToken cancellationToken)
	{
		if (Platform.CurrentActivity is not MainActivity activity)
		{
			return null;
		}

		string? uriString = await activity.PickFolderUriAsync();
		cancellationToken.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(uriString))
		{
			return null;
		}

		string destination = Path.Combine(FileSystem.CacheDirectory, "DisplayBookFolderSelections", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(destination);
		try
		{
			Android.Net.Uri treeUri = Android.Net.Uri.Parse(uriString) ?? throw new IOException("Android returned an invalid folder URI.");
			string documentId = DocumentsContract.GetTreeDocumentId(treeUri) ?? throw new IOException("Android returned an invalid folder document.");
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

	static async Task CopyDocumentTreeAsync(
		Android.Content.ContentResolver resolver,
		Android.Net.Uri treeUri,
		string documentId,
		string destination,
		IProgress<BookImportProgress>? progress,
		CancellationToken cancellationToken)
	{
		Android.Net.Uri childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, documentId)
			?? throw new IOException("Android could not enumerate the selected folder.");
		string[] projection = new[] { "document_id", "_display_name", "mime_type" };
		using ICursor cursor = resolver.Query(childrenUri, projection, null, null, null) ?? throw new IOException("Android could not read the selected folder.");
		while (cursor.MoveToNext())
		{
			cancellationToken.ThrowIfCancellationRequested();
			string? childId = cursor.GetString(cursor.GetColumnIndexOrThrow("document_id"));
			string? displayName = cursor.GetString(cursor.GetColumnIndexOrThrow("_display_name"));
			string? mimeType = cursor.GetString(cursor.GetColumnIndexOrThrow("mime_type"));
			if (string.IsNullOrWhiteSpace(childId) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(mimeType))
			{
				continue;
			}
			string safeName = Path.GetFileName(displayName);
			if (string.IsNullOrWhiteSpace(safeName))
			{
				continue;
			}

			Android.Net.Uri childUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, childId)
				?? throw new IOException("Android could not open an item in the selected folder.");
			string destinationPath = EpubPackageReader.ResolveWithinRoot(destination, safeName);
			if (string.Equals(mimeType, DocumentsContract.Document.MimeTypeDir, StringComparison.OrdinalIgnoreCase))
			{
				Directory.CreateDirectory(destinationPath);
				await CopyDocumentTreeAsync(resolver, treeUri, childId, destinationPath, progress, cancellationToken);
				continue;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
			await using Stream? input = resolver.OpenInputStream(childUri);
			if (input is null)
			{
				continue;
			}

			await using FileStream output = File.Create(destinationPath);
			await input.CopyToAsync(output, cancellationToken);
			progress?.Report(new BookImportProgress("Copying selected folder", displayName, 0, 0));
		}
	}
}
