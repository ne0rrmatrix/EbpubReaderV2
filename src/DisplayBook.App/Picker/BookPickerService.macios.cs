using DisplayBook.App.Models;
using Foundation;
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

		UIViewController presenter = Platform.GetCurrentUIViewController()
			?? throw new InvalidOperationException("The iOS folder picker has no presenting view controller.");
		UIDocumentPickerViewController picker = new(
			[UTType.CreateFromIdentifier("public.folder") ?? throw new InvalidOperationException("Apple could not create the folder type.")],
			asCopy: true);
		FolderPickerDelegate pickerDelegate = new();
		picker.Delegate = pickerDelegate;

		using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() => pickerDelegate.Cancel(cancellationToken));
		await MainThread.InvokeOnMainThreadAsync(() => presenter.PresentViewController(picker, true, null));

		NSUrl? selectedUrl = await pickerDelegate.Result.Task;
		if (selectedUrl is null)
		{
			return null;
		}

		string? sourceRoot = selectedUrl.Path;
		if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
		{
			throw new IOException("iOS returned an invalid folder path.");
		}

		bool hasSecurityScope = selectedUrl.StartAccessingSecurityScopedResource();
		string destination = Path.Combine(
			FileSystem.CacheDirectory,
			"DisplayBookFolderSelections",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(destination);

		try
		{
			string[] files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToArray();
			for (int index = 0; index < files.Length; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				string sourcePath = files[index];
				string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
				string destinationPath = EpubPackageReader.ResolveWithinRoot(destination, relativePath);
				Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

				await using FileStream input = File.OpenRead(sourcePath);
				await using FileStream output = File.Create(destinationPath);
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

	sealed class FolderPickerDelegate : UIDocumentPickerDelegate
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