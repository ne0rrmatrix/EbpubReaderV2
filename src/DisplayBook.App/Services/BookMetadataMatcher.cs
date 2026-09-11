using System.Globalization;
using System.Text;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public static class BookMetadataMatcher
{
	public static bool Matches(BookSummary book, string? title, string? author)
	{
		ArgumentNullException.ThrowIfNull(book);
		return Matches(book.Title, book.Author, title, author);
	}

	public static bool Matches(string? libraryTitle, string? libraryAuthor, string? title, string? author)
	{
		string normalizedLibraryTitle = Normalize(libraryTitle);
		string normalizedLibraryAuthor = Normalize(libraryAuthor);
		string normalizedTitle = Normalize(title);
		string normalizedAuthor = Normalize(author);
		return normalizedLibraryTitle.Length > 0 &&
			normalizedLibraryAuthor.Length > 0 &&
			normalizedLibraryTitle == normalizedTitle &&
			normalizedLibraryAuthor == normalizedAuthor;
	}

	public static string Normalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}

		StringBuilder builder = new();
		foreach (char character in value.Normalize(NormalizationForm.FormD))
		{
			if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}

			if (char.IsLetterOrDigit(character))
			{
				builder.Append(char.ToLowerInvariant(character));
			}
		}

		return builder.ToString();
	}
}