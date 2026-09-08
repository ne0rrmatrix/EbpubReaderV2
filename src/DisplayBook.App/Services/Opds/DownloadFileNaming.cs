using System.Security.Cryptography;
using System.Text;
using DisplayBook.App.Models;

namespace DisplayBook.App.Services.Opds;

public static class DownloadFileNaming
{
    private const int HashLength = 16;
    private const int MaximumSlugLength = 72;

    public static string GetFileName(DownloadLink link, string bookTitle)
    {
        ArgumentNullException.ThrowIfNull(link);

        var slug = CreateSlug(string.IsNullOrWhiteSpace(bookTitle) ? link.Title : bookTitle);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link.Url.Trim())))[..HashLength].ToLowerInvariant();
        return $"{slug}-{hash}{GetExtension(link)}";
    }

    private static string CreateSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "download";
        }

        var builder = new StringBuilder();
        var pendingSeparator = false;
        foreach (var character in value.Normalize(NormalizationForm.FormD))
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

            if (builder.Length >= MaximumSlugLength)
            {
                break;
            }
        }

        return builder.Length == 0 ? "download" : builder.ToString().TrimEnd('-');
    }

    private static string GetExtension(DownloadLink link)
    {
        var mime = link.Format?.Split(';', 2)[0].Trim().ToLowerInvariant();
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

    private static string? GetUrlExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // Path.GetExtension includes the leading dot, so validate the dot-less suffix and
        // return the extension as produced (dot included).
        var extension = Path.GetExtension(uri.AbsolutePath);
        var suffix = extension.TrimStart('.');
        return suffix.Length is > 0 and <= 10 && suffix.All(character => char.IsLetterOrDigit(character))
            ? extension.ToLowerInvariant()
            : null;
    }

    private static string GetFormatNameExtension(string? formatName)
    {
        var value = formatName?.Trim().ToLowerInvariant();
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