namespace DisplayBook.App.Services.BookMetadata;

public enum BookIdentifierKind
{
	Isbn10,
	Isbn13,
	Unknown,
}

/// <summary>
/// ISBN-10/13 cleaning, validation, and cross-conversion. Trimmed from the earlier
/// Amazon-era identifier module: no ASIN classification or region/URL building —
/// neither Google Books nor Open Library have an Amazon-style region concept, and
/// both accept a plain ISBN string directly.
/// </summary>
public static class BookIdentifiers
{
	public static string Clean(string? raw)
	{
		if (raw is null)
		{
			return string.Empty;
		}

		string s = raw.Trim().Replace("-", string.Empty).Replace(" ", string.Empty);
		if (s.StartsWith("isbn", StringComparison.OrdinalIgnoreCase))
		{
			s = s[4..];
		}

		return s;
	}

	public static bool IsIsbn10(string candidate) =>
		candidate.Length == 10 &&
		candidate[..9].All(char.IsDigit) &&
		(char.IsDigit(candidate[9]) || candidate[9] is 'x' or 'X');

	public static bool IsIsbn13(string candidate) =>
		candidate.Length == 13 && candidate.All(char.IsDigit);

	public static bool Isbn10ChecksumValid(string candidate)
	{
		int total = 0;
		for (int i = 0; i < candidate.Length; i++)
		{
			char ch = candidate[i];
			int digit = ch is 'x' or 'X' ? 10 : ch - '0';
			total += digit * (10 - i);
		}

		return total % 11 == 0;
	}

	public static bool Isbn13ChecksumValid(string candidate)
	{
		int total = 0;
		for (int i = 0; i < candidate.Length; i++)
		{
			int d = candidate[i] - '0';
			total += d * (i % 2 == 1 ? 3 : 1);
		}

		return total % 10 == 0;
	}

	/// <summary>Classifies a cleaned candidate, validating its checksum. Never throws.</summary>
	public static BookIdentifierKind Classify(string candidate)
	{
		return IsIsbn13(candidate) && Isbn13ChecksumValid(candidate)
			? BookIdentifierKind.Isbn13
			: IsIsbn10(candidate) && Isbn10ChecksumValid(candidate) ? BookIdentifierKind.Isbn10 : BookIdentifierKind.Unknown;
	}

	public static string? Isbn13ToIsbn10(string candidate)
	{
		if (!IsIsbn13(candidate))
		{
			return null;
		}

		if (!candidate.StartsWith("978", StringComparison.Ordinal) && !candidate.StartsWith("979", StringComparison.Ordinal))
		{
			return null;
		}

		string body = candidate[3..12];
		int total = 0;
		for (int i = 0; i < body.Length; i++)
		{
			total += (body[i] - '0') * (10 - i);
		}

		int rem = total % 11;
		int check = (11 - rem) % 11;
		string checkChar = check == 10 ? "X" : check.ToString();
		return body + checkChar;
	}
}
