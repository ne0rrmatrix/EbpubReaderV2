namespace DisplayBook.App.Services;

/// <summary>
/// Calibre content servers expose two cover endpoints for the same book: "/get/thumb/{book_id}/{library_id}?sz=WxH"
/// (a dynamically resized, low-resolution thumbnail meant for grid views) and "/get/cover/{book_id}/{library_id}"
/// (the original, full-resolution cover as stored in the library). OPDS feeds only ever advertise the thumb link,
/// so grid covers built from it look soft/pixelated once displayed larger than the thumb size. This rewrites a
/// thumb URL to request the original whenever it matches Calibre's convention; anything else passes through unchanged.
/// </summary>
static class CalibreCoverUrl
{
	const string thumbSegment = "/get/thumb/";
	const string coverSegment = "/get/cover/";

	public static string Upgrade(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return url ?? string.Empty;
		}

		int thumbIndex = url.IndexOf(thumbSegment, StringComparison.OrdinalIgnoreCase);
		if (thumbIndex < 0)
		{
			return url;
		}

		string afterSegment = url[(thumbIndex + thumbSegment.Length)..];
		int queryIndex = afterSegment.IndexOf('?');
		string bookAndLibraryPath = queryIndex >= 0 ? afterSegment[..queryIndex] : afterSegment;

		return string.Concat(url.AsSpan(0, thumbIndex), coverSegment, bookAndLibraryPath);
	}
}
