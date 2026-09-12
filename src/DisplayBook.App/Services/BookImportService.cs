using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services;

public sealed class BookImportService(
	IBookPickerService picker,
	IBookCatalogService catalog,
	BookStorageService storage,
	ILogger<BookImportService> logger) : IBookImportService
{
	const string checkingExistingLibraryStage = "Checking existing library";
	const string importingBooksStage = "Importing books";

	public async Task<IReadOnlyList<BookSummary>> ImportFileAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		FileResult? file = await picker.PickEpubFileAsync(cancellationToken);
		if (file is null)
		{
			return [];
		}

		ValidateEpubFileName(file.FileName);
		await using Stream source = await file.OpenReadAsync();
		return await ImportArchiveAsync(source, file.FileName, progress, cancellationToken);
	}

	public async Task<IReadOnlyList<BookSummary>> ImportLocalFileAsync(
		string filePath,
		IProgress<BookImportProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
		ValidateEpubFileName(filePath);
		if (!File.Exists(filePath))
		{
			throw new FileNotFoundException("The downloaded EPUB no longer exists.", filePath);
		}

		await using FileStream source = new(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			bufferSize: 81920,
			useAsync: true);
		return await ImportArchiveAsync(source, Path.GetFileName(filePath), progress, cancellationToken);
	}

	async Task<IReadOnlyList<BookSummary>> ImportArchiveAsync(
		Stream source,
		string originalFileName,
		IProgress<BookImportProgress>? progress,
		CancellationToken cancellationToken)
	{
		string importId = Guid.NewGuid().ToString("N");
		string importRoot = Path.Combine(FileSystem.CacheDirectory, "DisplayBookImports", importId);
		string stagingRoot = Path.Combine(importRoot, "Book");
		Directory.CreateDirectory(stagingRoot);
		try
		{
			ReportProgress(progress, "Importing book", originalFileName, 0, 1);
			using ZipArchive archive = new(source, ZipArchiveMode.Read, leaveOpen: true);
			ExtractArchive(archive, stagingRoot, cancellationToken);
			string contentHash = await ComputeDirectoryHashAsync(stagingRoot, cancellationToken);
			HashSet<string> knownHashes = await GetKnownContentHashesAsync(progress, cancellationToken);
			BookSummary? book = await SaveImportedBookAsync(stagingRoot, originalFileName, importId, contentHash, knownHashes, cancellationToken);
			ReportProgress(progress, "Import complete", book?.Title ?? originalFileName, 1, 1);
			return book is null ? [] : [book];
		}
		finally
		{
			DeleteDirectory(importRoot);
		}
	}

	public async Task<IReadOnlyList<BookSummary>> ImportFolderAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default)
	{
		string? sourceRoot = await picker.PickFolderAsync(progress, cancellationToken);
		if (string.IsNullOrWhiteSpace(sourceRoot))
		{
			return [];
		}

		string importId = Guid.NewGuid().ToString("N");
		string importRoot = Path.Combine(FileSystem.CacheDirectory, "DisplayBookImports", importId);
		try
		{
			ReportProgress(progress, "Scanning selected folder", "Looking for EPUB files", 0, 0);
			List<ImportCandidate> candidates = FindCandidates(sourceRoot);
			if (candidates.Count == 0)
			{
				throw new InvalidDataException("The selected folder does not contain any .epub files.");
			}

			ReportProgress(progress, "Scanning selected folder", $"Found {candidates.Count:N0} EPUB files", candidates.Count, candidates.Count);
			HashSet<string> knownHashes = await GetKnownContentHashesAsync(progress, cancellationToken);
			List<BookSummary> importedBooks = [];
			List<string> failures = [];
			ReportProgress(progress, importingBooksStage, "Starting import", 0, candidates.Count);
			for (int index = 0; index < candidates.Count; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				ImportCandidate candidate = candidates[index];
				ReportProgress(progress, importingBooksStage, Path.GetFileName(candidate.Path), index, candidates.Count);
				BookSummary? book = await ImportCandidateAsync(
					candidate,
					importRoot,
					knownHashes,
					failures,
					progress,
					new CandidateProgress(index, candidates.Count),
					cancellationToken);
				if (book is not null)
				{
					importedBooks.Add(book);
				}

				ReportProgress(progress, importingBooksStage, book?.Title ?? Path.GetFileName(candidate.Path), index + 1, candidates.Count);
			}

			if (importedBooks.Count == 0 && failures.Count > 0)
			{
				throw new InvalidDataException($"No valid new EPUBs could be imported. {failures[0]}");
			}

			logger.LogInformation("Imported {ImportedCount} EPUBs from folder {SourceRoot}; skipped {SkippedCount} candidates.",
				importedBooks.Count,
				sourceRoot,
				candidates.Count - importedBooks.Count);
			return importedBooks;
		}
		finally
		{
			DeleteDirectory(importRoot);
			if (IsChildPathOf(sourceRoot, FileSystem.CacheDirectory))
			{
				DeleteDirectory(sourceRoot);
			}
		}
	}

	async Task<BookSummary?> SaveImportedBookAsync(
		string stagingRoot,
		string originalFileName,
		string importId,
		string contentHash,
		HashSet<string> knownHashes,
		CancellationToken cancellationToken)
	{
		if (knownHashes.Contains(contentHash) || await catalog.ContainsContentHashAsync(contentHash, cancellationToken))
		{
			logger.LogInformation("Skipped duplicate EPUB {FileName}.", originalFileName);
			DeleteDirectory(stagingRoot);
			return null;
		}

		EpubPackageMetadata metadata = EpubPackageReader.Read(stagingRoot);
		await BookStorageService.InitializeAsync(cancellationToken);
		string bookId = importId;
		string finalRoot = BookStorageService.GetBookRoot(bookId);
		if (Directory.Exists(finalRoot))
		{
			throw new IOException("A storage directory already exists for this book.");
		}

		Directory.CreateDirectory(BookStorageService.BooksRoot);
		Directory.Move(stagingRoot, finalRoot);
		BookSummary summary = new(
			bookId,
			metadata.Title,
			metadata.Author,
			metadata.Description,
			metadata.Language,
			metadata.Publisher,
			string.IsNullOrWhiteSpace(metadata.CoverRelativePath)
				? string.Empty
				: BookStorageService.GetAbsolutePath($"Books/{bookId}/{metadata.CoverRelativePath}"),
			$"Books/{bookId}",
			metadata.OpfRelativePath,
			originalFileName,
			DateTimeOffset.UtcNow,
			null,
			string.Empty,
			0,
			1,
			contentHash,
			metadata.Isbn);

		try
		{
			await catalog.AddBookAsync(summary, string.IsNullOrWhiteSpace(metadata.CoverRelativePath) ? string.Empty : $"Books/{bookId}/{metadata.CoverRelativePath}", cancellationToken);
			knownHashes.Add(contentHash);
			logger.LogInformation("Imported EPUB {Title} into {Path}.", summary.Title, finalRoot);
			return summary;
		}
		catch
		{
			DeleteDirectory(finalRoot);
			throw;
		}
	}

	async Task<BookSummary?> ImportCandidateAsync(
		ImportCandidate candidate,
		string importRoot,
		HashSet<string> knownHashes,
		List<string> failures,
		IProgress<BookImportProgress>? progress,
		CandidateProgress candidateProgress,
		CancellationToken cancellationToken)
	{
		string candidateRoot = Path.Combine(importRoot, "Books", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(candidateRoot);
		try
		{
			if (candidate.IsDirectory)
			{
				CopyDirectory(candidate.Path, candidateRoot, progress, cancellationToken);
			}
			else
			{
				ReportProgress(
					progress,
					importingBooksStage,
					$"Reading {Path.GetFileName(candidate.Path)}",
					candidateProgress.Index,
					candidateProgress.Total);
				await using FileStream source = File.OpenRead(candidate.Path);
				using ZipArchive archive = new(source, ZipArchiveMode.Read, leaveOpen: false);
				ExtractArchive(archive, candidateRoot, cancellationToken);
			}

			string contentHash = await ComputeDirectoryHashAsync(candidateRoot, cancellationToken);
			return await SaveImportedBookAsync(
				candidateRoot,
				Path.GetFileName(candidate.Path),
				Guid.NewGuid().ToString("N"),
				contentHash,
				knownHashes,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			string candidateName = Path.GetFileName(candidate.Path);
			failures.Add($"{candidateName}: {exception.Message}");
			logger.LogWarning(exception, "Skipped EPUB candidate {CandidatePath}.", candidate.Path);
			return null;
		}
	}

	async Task<HashSet<string>> GetKnownContentHashesAsync(IProgress<BookImportProgress>? progress, CancellationToken cancellationToken)
	{
		HashSet<string> knownHashes = [with(StringComparer.OrdinalIgnoreCase)];
		IReadOnlyList<BookSummary> books = await catalog.GetBooksAsync(cancellationToken);
		ReportProgress(progress, checkingExistingLibraryStage, "Preparing duplicate check", 0, books.Count);
		for (int index = 0; index < books.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			BookSummary book = books[index];
			ReportProgress(progress, checkingExistingLibraryStage, book.Title, index, books.Count);
			if (!string.IsNullOrWhiteSpace(book.ContentHash))
			{
				knownHashes.Add(book.ContentHash);
				ReportProgress(progress, checkingExistingLibraryStage, book.Title, index + 1, books.Count);
				continue;
			}

			string existingRoot = BookStorageService.GetAbsolutePath(book.PublicationRoot);
			if (!Directory.Exists(existingRoot))
			{
				continue;
			}

			try
			{
				knownHashes.Add(await ComputeDirectoryHashAsync(existingRoot, cancellationToken));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				logger.LogWarning(exception, "Could not fingerprint existing EPUB {BookId}.", book.Id);
			}

			ReportProgress(progress, checkingExistingLibraryStage, book.Title, index + 1, books.Count);
		}

		return knownHashes;
	}

	static List<ImportCandidate> FindCandidates(string sourceRoot)
	{
		return IsUnpackedEpub(sourceRoot)
			? [new ImportCandidate(sourceRoot, true)]
			: [.. Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
			.Where(path => string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.Select(path => new ImportCandidate(path, false))];
	}

	static bool IsUnpackedEpub(string path)
	{
		return File.Exists(Path.Combine(path, "META-INF", "container.xml"));
	}

	static async Task<string> ComputeDirectoryHashAsync(string root, CancellationToken cancellationToken)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Select(path => new
			{
				Path = path,
				RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/')
			})
			.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
			.ToList();
		byte[] separator = [0];
		byte[] buffer = new byte[81920];

		foreach (var file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
			hash.AppendData(separator);
			await using FileStream stream = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
			int bytesRead;
			while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
			{
				hash.AppendData(buffer, 0, bytesRead);
			}
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}

	static void ExtractArchive(ZipArchive archive, string destinationRoot, CancellationToken cancellationToken)
	{
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string entryPath = EpubPackageReader.ResolveWithinRoot(destinationRoot, entry.FullName);
			if (string.IsNullOrEmpty(entry.Name))
			{
				Directory.CreateDirectory(entryPath);
				continue;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
			using Stream input = entry.Open();
			using FileStream output = File.Create(entryPath);
			input.CopyTo(output);
		}
	}

	static void CopyDirectory(
		string sourceRoot,
		string destinationRoot,
		IProgress<BookImportProgress>? progress,
		CancellationToken cancellationToken)
	{
		if (!Directory.Exists(sourceRoot))
		{
			throw new DirectoryNotFoundException("The selected EPUB folder no longer exists.");
		}

		foreach (string directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
		{
			cancellationToken.ThrowIfCancellationRequested();
			string relative = Path.GetRelativePath(sourceRoot, directory);
			string destination = EpubPackageReader.ResolveWithinRoot(destinationRoot, relative);
			Directory.CreateDirectory(destination);
		}

		List<string> files = [.. Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)];
		ReportProgress(progress, "Copying selected folder", "Starting folder copy", 0, files.Count);
		for (int index = 0; index < files.Count; index++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string file = files[index];
			string relative = Path.GetRelativePath(sourceRoot, file);
			string destination = EpubPackageReader.ResolveWithinRoot(destinationRoot, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(file, destination, overwrite: false);
			ReportProgress(progress, "Copying selected folder", relative, index + 1, files.Count);
		}
	}

	static void ReportProgress(
		IProgress<BookImportProgress>? progress,
		string stage,
		string currentItem,
		int completed,
		int total)
	{
		progress?.Report(new BookImportProgress(stage, currentItem, completed, total));
	}

	static void ValidateEpubFileName(string fileName)
	{
		if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("Please choose a file with the .epub extension.");
		}
	}

	static void DeleteDirectory(string path)
	{
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
	}

	static bool IsChildPathOf(string path, string parent)
	{
		string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string parentWithSeparator = fullParent + Path.DirectorySeparatorChar;
		return fullPath.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
	}

	sealed record ImportCandidate(string Path, bool IsDirectory);

	readonly record struct CandidateProgress(int Index, int Total);
}