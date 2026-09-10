using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using DisplayBook.App.Models;
using Microsoft.Maui.Storage;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services;

public sealed class BookImportService(
    IBookPickerService picker,
    IBookCatalogService catalog,
    BookStorageService storage,
    ILogger<BookImportService> logger) : IBookImportService
{
    private const string CheckingExistingLibraryStage = "Checking existing library";
    private const string ImportingBooksStage = "Importing books";

    public async Task<IReadOnlyList<BookSummary>> ImportFileAsync(IProgress<BookImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var file = await picker.PickEpubFileAsync(cancellationToken);
        if (file is null)
        {
            return [];
        }

        ValidateEpubFileName(file.FileName);
        await using var source = await file.OpenReadAsync();
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

        await using var source = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        return await ImportArchiveAsync(source, Path.GetFileName(filePath), progress, cancellationToken);
    }

    private async Task<IReadOnlyList<BookSummary>> ImportArchiveAsync(
        Stream source,
        string originalFileName,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var importId = Guid.NewGuid().ToString("N");
        var importRoot = Path.Combine(FileSystem.CacheDirectory, "DisplayBookImports", importId);
        var stagingRoot = Path.Combine(importRoot, "Book");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            ReportProgress(progress, "Importing book", originalFileName, 0, 1);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            ExtractArchive(archive, stagingRoot, cancellationToken);
            var contentHash = await ComputeDirectoryHashAsync(stagingRoot, cancellationToken);
            var knownHashes = await GetKnownContentHashesAsync(progress, cancellationToken);
            var book = await SaveImportedBookAsync(stagingRoot, originalFileName, importId, contentHash, knownHashes, cancellationToken);
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
        var sourceRoot = await picker.PickFolderAsync(progress, cancellationToken);
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return [];
        }

        var importId = Guid.NewGuid().ToString("N");
        var importRoot = Path.Combine(FileSystem.CacheDirectory, "DisplayBookImports", importId);
        try
        {
            ReportProgress(progress, "Scanning selected folder", "Looking for EPUB files", 0, 0);
            var candidates = FindCandidates(sourceRoot);
            if (candidates.Count == 0)
            {
                throw new InvalidDataException("The selected folder does not contain any .epub files.");
            }

            ReportProgress(progress, "Scanning selected folder", $"Found {candidates.Count:N0} EPUB files", candidates.Count, candidates.Count);
            var knownHashes = await GetKnownContentHashesAsync(progress, cancellationToken);
            var importedBooks = new List<BookSummary>();
            var failures = new List<string>();
            ReportProgress(progress, ImportingBooksStage, "Starting import", 0, candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = candidates[index];
                ReportProgress(progress, ImportingBooksStage, Path.GetFileName(candidate.Path), index, candidates.Count);
                var book = await ImportCandidateAsync(
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

                ReportProgress(progress, ImportingBooksStage, book?.Title ?? Path.GetFileName(candidate.Path), index + 1, candidates.Count);
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

    private async Task<BookSummary?> SaveImportedBookAsync(
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

        var metadata = EpubPackageReader.Read(stagingRoot);
        await storage.InitializeAsync(cancellationToken);
        var bookId = importId;
        var finalRoot = storage.GetBookRoot(bookId);
        if (Directory.Exists(finalRoot))
        {
            throw new IOException("A storage directory already exists for this book.");
        }

        Directory.CreateDirectory(storage.BooksRoot);
        Directory.Move(stagingRoot, finalRoot);
        var summary = new BookSummary(
            bookId,
            metadata.Title,
            metadata.Author,
            metadata.Description,
            metadata.Language,
            metadata.Publisher,
            string.IsNullOrWhiteSpace(metadata.CoverRelativePath)
                ? string.Empty
                : storage.GetAbsolutePath($"Books/{bookId}/{metadata.CoverRelativePath}"),
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

    private async Task<BookSummary?> ImportCandidateAsync(
        ImportCandidate candidate,
        string importRoot,
        HashSet<string> knownHashes,
        List<string> failures,
        IProgress<BookImportProgress>? progress,
        CandidateProgress candidateProgress,
        CancellationToken cancellationToken)
    {
        var candidateRoot = Path.Combine(importRoot, "Books", Guid.NewGuid().ToString("N"));
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
                    ImportingBooksStage,
                    $"Reading {Path.GetFileName(candidate.Path)}",
                    candidateProgress.Index,
                    candidateProgress.Total);
                await using var source = File.OpenRead(candidate.Path);
                using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
                ExtractArchive(archive, candidateRoot, cancellationToken);
            }

            var contentHash = await ComputeDirectoryHashAsync(candidateRoot, cancellationToken);
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
            var candidateName = Path.GetFileName(candidate.Path);
            failures.Add($"{candidateName}: {exception.Message}");
            logger.LogWarning(exception, "Skipped EPUB candidate {CandidatePath}.", candidate.Path);
            return null;
        }
    }

    private async Task<HashSet<string>> GetKnownContentHashesAsync(IProgress<BookImportProgress>? progress, CancellationToken cancellationToken)
    {
        var knownHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var books = await catalog.GetBooksAsync(cancellationToken);
        ReportProgress(progress, CheckingExistingLibraryStage, "Preparing duplicate check", 0, books.Count);
        for (var index = 0; index < books.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var book = books[index];
            ReportProgress(progress, CheckingExistingLibraryStage, book.Title, index, books.Count);
            if (!string.IsNullOrWhiteSpace(book.ContentHash))
            {
                knownHashes.Add(book.ContentHash);
                ReportProgress(progress, CheckingExistingLibraryStage, book.Title, index + 1, books.Count);
                continue;
            }

            var existingRoot = storage.GetAbsolutePath(book.PublicationRoot);
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

            ReportProgress(progress, CheckingExistingLibraryStage, book.Title, index + 1, books.Count);
        }

        return knownHashes;
    }

    private static List<ImportCandidate> FindCandidates(string sourceRoot)
    {
        if (IsUnpackedEpub(sourceRoot))
        {
            return [new ImportCandidate(sourceRoot, true)];
        }

        return [.. Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new ImportCandidate(path, false))];
    }

    private static bool IsUnpackedEpub(string path)
    {
        return File.Exists(Path.Combine(path, "META-INF", "container.xml"));
    }

    private static async Task<string> ComputeDirectoryHashAsync(string root, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/')
            })
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToList();
        var separator = new byte[] { 0 };
        var buffer = new byte[81920];

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData(separator);
            await using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, useAsync: true);
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, bytesRead);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ExtractArchive(ZipArchive archive, string destinationRoot, CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPath = EpubPackageReader.ResolveWithinRoot(destinationRoot, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(entryPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            using var input = entry.Open();
            using var output = File.Create(entryPath);
            input.CopyTo(output);
        }
    }

    private static void CopyDirectory(
        string sourceRoot,
        string destinationRoot,
        IProgress<BookImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException("The selected EPUB folder no longer exists.");
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, directory);
            var destination = EpubPackageReader.ResolveWithinRoot(destinationRoot, relative);
            Directory.CreateDirectory(destination);
        }

        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToList();
        ReportProgress(progress, "Copying selected folder", "Starting folder copy", 0, files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            var relative = Path.GetRelativePath(sourceRoot, file);
            var destination = EpubPackageReader.ResolveWithinRoot(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
            ReportProgress(progress, "Copying selected folder", relative, index + 1, files.Count);
        }
    }

    private static void ReportProgress(
        IProgress<BookImportProgress>? progress,
        string stage,
        string currentItem,
        int completed,
        int total)
    {
        progress?.Report(new BookImportProgress(stage, currentItem, completed, total));
    }

    private static void ValidateEpubFileName(string fileName)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".epub", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Please choose a file with the .epub extension.");
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static bool IsChildPathOf(string path, string parent)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentWithSeparator = fullParent + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ImportCandidate(string Path, bool IsDirectory);

    private readonly record struct CandidateProgress(int Index, int Total);
}
