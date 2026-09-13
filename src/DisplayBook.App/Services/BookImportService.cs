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
		Directory.CreateDirectory(importRoot);
		string stagingEpubPath = Path.Combine(importRoot, "Book.epub");
		try
		{
			ReportProgress(progress, "Importing book", originalFileName, 0, 1);
			await using (FileStream staging = File.Create(stagingEpubPath))
			{
				await source.CopyToAsync(staging, cancellationToken);
			}

			string contentHash = await ComputeArchiveHashAsync(stagingEpubPath, cancellationToken);
			HashSet<string> knownHashes = await GetKnownContentHashesAsync(progress, cancellationToken);
			BookSummary? book = await SaveImportedBookAsync(stagingEpubPath, originalFileName, importId, contentHash, knownHashes, cancellationToken);
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
		string stagingEpubPath,
		string originalFileName,
		string importId,
		string contentHash,
		HashSet<string> knownHashes,
		CancellationToken cancellationToken)
	{
		if (knownHashes.Contains(contentHash) || await catalog.ContainsContentHashAsync(contentHash, cancellationToken))
		{
			logger.LogInformation("Skipped duplicate EPUB {FileName}.", originalFileName);
			DeleteFile(stagingEpubPath);
			return null;
		}

		string bookId = importId;
		(EpubPackageMetadata metadata, string coverRelativePath, string coverAbsolutePath) = ReadMetadataAndExtractCover(stagingEpubPath, bookId);

		await BookStorageService.InitializeAsync(cancellationToken);
		string finalEpubPath = BookStorageService.GetBookFilePath(bookId);
		if (File.Exists(finalEpubPath))
		{
			throw new IOException("A storage file already exists for this book.");
		}

		Directory.CreateDirectory(BookStorageService.BooksRoot);
		File.Move(stagingEpubPath, finalEpubPath);

		BookSummary summary = new(
			bookId,
			metadata.Title,
			metadata.Author,
			metadata.Description,
			metadata.Language,
			metadata.Publisher,
			coverAbsolutePath,
			$"Books/{bookId}.epub",
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
			await catalog.AddBookAsync(summary, coverRelativePath, cancellationToken);
			knownHashes.Add(contentHash);
			logger.LogInformation("Imported EPUB {Title} into {Path}.", summary.Title, finalEpubPath);
			return summary;
		}
		catch
		{
			DeleteFile(finalEpubPath);
			DeleteFile(coverAbsolutePath);
			throw;
		}
	}

	/// <summary>
	/// Reads title/author/cover metadata and, if the EPUB declares a cover image, extracts just
	/// that one file to its own small persisted location (<see cref="BookStorageService.GetCoverFilePath"/>).
	/// Everything else in the archive is left untouched on disk -- chapters/CSS/fonts/other
	/// images are parsed into memory fresh each time the book is opened for reading instead.
	/// </summary>
	static (EpubPackageMetadata Metadata, string CoverRelativePath, string CoverAbsolutePath) ReadMetadataAndExtractCover(string epubFilePath, string bookId)
	{
		using FileStream stream = File.OpenRead(epubFilePath);
		using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
		EpubPackageMetadata metadata = EpubPackageReader.Read(archive);

		if (string.IsNullOrWhiteSpace(metadata.CoverRelativePath))
		{
			return (metadata, string.Empty, string.Empty);
		}

		ZipArchiveEntry? coverEntry = archive.GetEntry(metadata.CoverRelativePath);
		if (coverEntry is null)
		{
			return (metadata, string.Empty, string.Empty);
		}

		string extension = Path.GetExtension(metadata.CoverRelativePath);
		if (string.IsNullOrEmpty(extension))
		{
			extension = ".jpg";
		}

		string coverAbsolutePath = BookStorageService.GetCoverFilePath(bookId, extension);
		Directory.CreateDirectory(BookStorageService.CoversRoot);
		using (Stream entryStream = coverEntry.Open())
		using (FileStream output = File.Create(coverAbsolutePath))
		{
			entryStream.CopyTo(output);
		}

		return (metadata, BookStorageService.GetCoverRelativePath(bookId, extension), coverAbsolutePath);
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
		string stagingEpubPath = Path.Combine(importRoot, "Books", $"{Guid.NewGuid():N}.epub");
		Directory.CreateDirectory(Path.GetDirectoryName(stagingEpubPath)!);
		try
		{
			if (candidate.IsDirectory)
			{
				ReportProgress(
					progress,
					importingBooksStage,
					$"Packaging {Path.GetFileName(candidate.Path)}",
					candidateProgress.Index,
					candidateProgress.Total);
				CreateArchiveFromDirectory(candidate.Path, stagingEpubPath, cancellationToken);
			}
			else
			{
				ReportProgress(
					progress,
					importingBooksStage,
					$"Reading {Path.GetFileName(candidate.Path)}",
					candidateProgress.Index,
					candidateProgress.Total);
				File.Copy(candidate.Path, stagingEpubPath, overwrite: false);
			}

			string contentHash = await ComputeArchiveHashAsync(stagingEpubPath, cancellationToken);
			return await SaveImportedBookAsync(
				stagingEpubPath,
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
		HashSet<string> knownHashes = new(StringComparer.OrdinalIgnoreCase);
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

			// Legacy fallback for rows imported before ContentHash existed: fingerprint the
			// persisted .epub directly instead of failing the duplicate check outright.
			string existingEpubPath = BookStorageService.GetAbsolutePath(book.EpubRelativePath);
			if (!File.Exists(existingEpubPath))
			{
				continue;
			}

			try
			{
				knownHashes.Add(await ComputeArchiveHashAsync(existingEpubPath, cancellationToken));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
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

	/// <summary>
	/// Relative-path-ordered SHA-256 over the archive's entries (not the raw .epub bytes) so two
	/// zips of byte-identical content hash the same even if their container/compression differs
	/// across tools or devices -- see CLAUDE.md's note on cross-device sync determinism.
	/// </summary>
	static async Task<string> ComputeArchiveHashAsync(string epubFilePath, CancellationToken cancellationToken)
	{
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		byte[] separator = [0];
		byte[] buffer = new byte[81920];

		await using FileStream fileStream = new(epubFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
		using ZipArchive archive = new(fileStream, ZipArchiveMode.Read, leaveOpen: false);
		var entries = archive.Entries
			.Where(entry => !string.IsNullOrEmpty(entry.Name))
			.Select(entry => new { Entry = entry, RelativePath = entry.FullName.Replace('\\', '/') })
			.OrderBy(item => item.RelativePath, StringComparer.Ordinal)
			.ToList();

		foreach (var item in entries)
		{
			cancellationToken.ThrowIfCancellationRequested();
			hash.AppendData(Encoding.UTF8.GetBytes(item.RelativePath));
			hash.AppendData(separator);
			using Stream entryStream = item.Entry.Open();
			int bytesRead;
			while ((bytesRead = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
			{
				hash.AppendData(buffer, 0, bytesRead);
			}
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}

	static void CreateArchiveFromDirectory(string sourceRoot, string destinationEpubPath, CancellationToken cancellationToken)
	{
		if (!Directory.Exists(sourceRoot))
		{
			throw new DirectoryNotFoundException("The selected EPUB folder no longer exists.");
		}

		using FileStream destination = File.Create(destinationEpubPath);
		using ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: false);
		foreach (string file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
		{
			cancellationToken.ThrowIfCancellationRequested();
			string relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
			archive.CreateEntryFromFile(file, relative);
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

	static void DeleteFile(string path)
	{
		if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
		{
			File.Delete(path);
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
