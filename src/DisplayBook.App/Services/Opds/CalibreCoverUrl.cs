namespace DisplayBook.App.Services;

/// <summary>
/// Calibre content servers expose two cover endpoints for the same book: "/get/thumb/{book_id}/{library_id}?sz=WxH"
/// (a dynamically resized, low-resolution thumbnail meant for grid views) and "/get/cover/{book_id}/{library_id}"
/// (the original, full-resolution cover as stored in the library). OPDS feeds only ever advertise the thumb link,
/// so grid covers built from it look soft/pixelated once displayed larger than the thumb size. This rewrites a
/// thumb URL to request the original whenever it matches Calibre's convention; anything else passes through unchanged.
/// </summary>
internal static class CalibreCoverUrl
{
    private const string ThumbSegment = "/get/thumb/";
    private const string CoverSegment = "/get/cover/";

    public static string Upgrade(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url ?? string.Empty;
        }

        var thumbIndex = url.IndexOf(ThumbSegment, StringComparison.OrdinalIgnoreCase);
        if (thumbIndex < 0)
        {
            return url;
        }

        var afterSegment = url[(thumbIndex + ThumbSegment.Length)..];
        var queryIndex = afterSegment.IndexOf('?');
        var bookAndLibraryPath = queryIndex >= 0 ? afterSegment[..queryIndex] : afterSegment;

        return string.Concat(url.AsSpan(0, thumbIndex), CoverSegment, bookAndLibraryPath);
    }
}
