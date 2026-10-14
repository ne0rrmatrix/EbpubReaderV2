namespace DisplayBook.App.Services.BookMetadata;

public sealed class BookMetadataRateLimitedException(string message) : Exception(message);

public sealed class BookMetadataFetchException(string message, Exception? inner = null) : Exception(message, inner);