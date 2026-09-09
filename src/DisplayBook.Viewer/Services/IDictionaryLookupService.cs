namespace DisplayBook.Viewer.Services;

/// <summary>A single dictionary result: the headword as displayed plus its definition.</summary>
public sealed record DictionaryDefinition(string Word, string Definition);

public interface IDictionaryLookupService
{
    /// <summary>
    /// Looks up a word from a raw text selection (only the first word of a multi-word
    /// selection is used). Returns <see langword="null"/> if nothing was selected or no
    /// definition was found.
    /// </summary>
    Task<DictionaryDefinition?> LookupAsync(string rawSelection, CancellationToken cancellationToken = default);
}
