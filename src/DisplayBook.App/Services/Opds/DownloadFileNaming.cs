using System.Security.Cryptography;
using System.Text;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services.Opds;

public static class DownloadFileNaming
{
	const int hashLength = 16;
	const int maximumSlugLength = 72;

	public static string GetFileName(DownloadLink link, string bookTitle)
	{
		ArgumentNullException.ThrowIfNull(link);

		string slug = CreateSlug(string.IsNullOrWhiteSpace(bookTitle) ? link.Title : bookTitle);
		string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link.Url.Trim())))[..hashLength].ToLowerInvariant();
		return $"{slug}-{hash}{GetExtension(link)}";
	}

	static string CreateSlug(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "download";
		}

		StringBuilder builder = new();
		bool pendingSeparator = false;
		foreach (char character in value.Normalize(NormalizationForm.FormD))
		{
			if (char.IsLetterOrDigit(character))
			{
				if (pendingSeparator && builder.Length > 0)
				{
					builder.Append('-');
				}

				builder.Append(char.ToLowerInvariant(character));
				pendingSeparator = false;
			}
			else if (builder.Length > 0)
			{
				pendingSeparator = true;
			}

			if (builder.Length >= maximumSlugLength)
			{
				break;
			}
		}

		return builder.Length == 0 ? "download" : builder.ToString().TrimEnd('-');
	}

	static string GetExtension(DownloadLink link)
	{
		string? mime = link.Format?.Split(';', 2)[0].Trim().ToLowerInvariant();
		return mime switch
		{
			"application/epub+zip" or "application/epub3" => ".epub",
			"application/pdf" => ".pdf",
			"application/mobi" or "application/x-mobipocket-ebook" or "application/x-mobi10-ebook" => ".mobi",
			"application/kindle+azw3" => ".azw3",
			"application/rtf" => ".rtf",
			"text/plain" => ".txt",
			_ => GetUrlExtension(link.Url) ?? GetFormatNameExtension(link.FormatName)
		};
	}

	static string? GetUrlExtension(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			return null;
		}

		// Path.GetExtension includes the leading dot, so validate the dot-less suffix and
		// return the extension as produced (dot included).
		string extension = Path.GetExtension(uri.AbsolutePath);
		string suffix = extension.TrimStart('.');
		return suffix.Length is > 0 and <= 10 && suffix.All(character => char.IsLetterOrDigit(character))
			? extension.ToLowerInvariant()
			: null;
	}

	static string GetFormatNameExtension(string? formatName)
	{
		string? value = formatName?.Trim().ToLowerInvariant();
		return value switch
		{
			"epub" => ".epub",
			"pdf" => ".pdf",
			"mobi" => ".mobi",
			"azw3" => ".azw3",
			"rtf" => ".rtf",
			"txt" => ".txt",
			_ => ".bin"
		};
	}
}