namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost : IReaderAssetHost
{
	static readonly string[] viewerAssetPaths =
	[
		"DisplayBookViewer/index.html",
		"DisplayBookViewer/EpubText.js",
		"DisplayBookViewer/EpubText.css",
		"DisplayBookViewer/ReadiumCSS-before.css",
		"DisplayBookViewer/ReadiumCSS-default.css",
		"DisplayBookViewer/ReadiumCSS-after.css"
	];
	static readonly SemaphoreSlim shellAssetsLock = new(1, 1);
	static IReadOnlyDictionary<string, byte[]>? shellAssets;

	/// <summary>
	/// Shared across every platform's resource handler so the extension-to-MIME-type mapping
	/// can't drift between them.
	/// </summary>
	internal static readonly Dictionary<string, string> MimeTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
	{
		[".html"] = "text/html",
		[".htm"] = "text/html",
		[".xhtml"] = "application/xhtml+xml",
		[".js"] = "text/javascript",
		[".mjs"] = "text/javascript",
		[".css"] = "text/css",
		[".json"] = "application/json",
		[".xml"] = "application/xml",
		[".opf"] = "application/oebps-package+xml",
		[".ncx"] = "application/x-dtbncx+xml",
		[".png"] = "image/png",
		[".jpg"] = "image/jpeg",
		[".jpeg"] = "image/jpeg",
		[".gif"] = "image/gif",
		[".svg"] = "image/svg+xml",
		[".webp"] = "image/webp",
		[".otf"] = "font/otf",
		[".ttf"] = "font/ttf",
		[".woff"] = "font/woff",
		[".woff2"] = "font/woff2",
		[".mp3"] = "audio/mpeg",
		[".m4a"] = "audio/mp4",
	};

	public async Task InitializeAsync(
		WebView webView,
		EpubArchive publicationSource,
		Func<string, Task>? navigationHandler = null,
		Action<string>? dictionaryLookupRequested = null,
		CancellationToken cancellationToken = default)
	{
		await EnsureShellAssetsLoadedAsync(cancellationToken);
		await ConfigurePlatformWebViewAsync(webView, publicationSource, navigationHandler, dictionaryLookupRequested, cancellationToken);
	}

	/// <summary>
	/// Reads the static viewer shell (index.html/EpubText.js/CSS) out of the app package into
	/// memory ahead of time. These six reads used to happen on the first
	/// <see cref="InitializeAsync"/> call -- that is, in the middle of opening the first book,
	/// where they were pure added latency. Nothing about them depends on which book is being
	/// opened, or on any book being opened at all, so the app calls this at startup instead.
	/// Safe and cheap to call more than once: the second call sees the assets are loaded and
	/// returns immediately.
	/// </summary>
	public static Task PreloadShellAssetsAsync(CancellationToken cancellationToken = default) =>
		EnsureShellAssetsLoadedAsync(cancellationToken);

	public Uri GetViewerUri(string opfRelativePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(opfRelativePath);
		return CreateViewerUri(opfRelativePath);
	}

	/// <summary>
	/// The reader shell's URL with no publication attached -- navigated to once per WebView
	/// instance (see <c>EpubReaderView.EnsureReaderShellLoadedAsync</c>) so index.html/EpubText.js
	/// finish booting before any book is opened. Once loaded, later books are handed to the
	/// already-running JS via <c>window.DisplayBookReader.loadPublication</c> instead of a fresh
	/// navigation to <see cref="GetViewerUri"/>.
	/// </summary>
	public Uri GetShellUri() => CreateShellUri();

	static string CreateOpfQuery(string opfRelativePath)
	{
		string relativeOpfPath = EpubPathUtilities.GetBookRelativePath(opfRelativePath);
		return Uri.EscapeDataString(relativeOpfPath);
	}

	static async Task EnsureShellAssetsLoadedAsync(CancellationToken cancellationToken)
	{
		if (shellAssets is not null)
		{
			return;
		}

		await shellAssetsLock.WaitAsync(cancellationToken);
		try
		{
			if (shellAssets is not null)
			{
				return;
			}

			Dictionary<string, byte[]> assets = new(StringComparer.OrdinalIgnoreCase);
			foreach (string assetPath in viewerAssetPaths)
			{
				await using Stream source = await FileSystem.OpenAppPackageFileAsync(assetPath);
				using MemoryStream buffer = new();
				await source.CopyToAsync(buffer, cancellationToken);
				assets[assetPath] = buffer.ToArray();
			}

			shellAssets = assets;
		}
		finally
		{
			shellAssetsLock.Release();
		}
	}

	/// <summary>
	/// Resolves a WebView resource request to bytes: the static viewer shell (index.html,
	/// EpubText.js, CSS) first, then the currently-open book's own content. Shared by every
	/// platform's resource handler so the "shell vs. book" merge exists in one place.
	/// </summary>
	internal static bool TryGetResourceBytes(EpubArchive publicationSource, string requestPath, out byte[] bytes)
	{
		string normalized = EpubArchive.NormalizeRequestPath(requestPath);
		if (shellAssets is not null && shellAssets.TryGetValue(normalized, out byte[]? shellBytes))
		{
			bytes = shellBytes;
			return true;
		}

		return publicationSource.TryGetEntry(normalized, out bytes!);
	}

	/// <summary>
	/// Custom WKWebView URL scheme used to serve reader content on iOS/MacCatalyst.
	/// A plain <c>file://</c> load only grants WKWebView read access to the folder
	/// containing the loaded page, which blocks <c>fetch()</c> calls to publication
	/// files served from the in-memory <see cref="EpubArchive"/>.
	/// </summary>
	internal const string AppleContentScheme = "displaybookcontent";
	internal const string AppleContentHost = "local";

	private static partial Task ConfigurePlatformWebViewAsync(
		WebView webView,
		EpubArchive publicationSource,
		Func<string, Task>? navigationHandler,
		Action<string>? dictionaryLookupRequested,
		CancellationToken cancellationToken);
	private static partial Uri CreateViewerUri(string opfRelativePath);
	private static partial Uri CreateShellUri();
}
