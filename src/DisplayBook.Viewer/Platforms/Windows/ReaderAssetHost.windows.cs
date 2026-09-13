using Microsoft.Web.WebView2.Core;

namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost
{
	const string hostName = "displaybook.local";

	const int lookUpMenuLabelMaxLength = 24;

	static readonly Dictionary<string, string> mimeTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
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

	private static async partial Task ConfigurePlatformWebViewAsync(
		Microsoft.Maui.Controls.WebView webView,
		string contentRoot,
		Func<string, Task>? navigationHandler,
		Action<string>? dictionaryLookupRequested,
		CancellationToken cancellationToken)
	{
		if (webView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 nativeWebView)
		{
			throw new InvalidOperationException("The Windows reader WebView is not ready for local content hosting.");
		}

		await nativeWebView.EnsureCoreWebView2Async();
		cancellationToken.ThrowIfCancellationRequested();

		// SetVirtualHostNameToFolderMapping resolves every request by hitting disk through
		// WebView2's own (Chromium) network stack -- subject to real-time antivirus scanning
		// on each file open, and with no caching (EpubText.js's fetch() calls use
		// { cache: "no-store" } so nothing is skipped either). For a book with many small
		// chapter/CSS/image files this adds up to several seconds, unlike iOS/Android, whose
		// asset loaders (WKUrlSchemeHandler / WebViewAssetLoader) don't go through a
		// disk-mapped network request at all -- see ReaderWebViewHandler.apple.cs's
		// ReaderContentSchemeHandler for the same in-memory-read approach used here.
		// WebResourceRequested lets us read the file into memory ourselves and hand back an
		// explicit response (with caching allowed), matching that same idea on Windows.
		string normalizedContentRoot = Path.GetFullPath(contentRoot);
		CoreWebView2 coreWebView = nativeWebView.CoreWebView2;
		coreWebView.AddWebResourceRequestedFilter($"https://{hostName}/*", CoreWebView2WebResourceContext.All);
		coreWebView.WebResourceRequested += async (_, args) =>
		{
			// WebResourceRequested handlers must respond synchronously unless a deferral is
			// taken; the deferral lets us read the file asynchronously and complete the
			// response later. `var` (rather than spelling out the deferral's type) sidesteps
			// the bundled WinUI3 WebView2 API surface not exposing that type the same way the
			// standalone Microsoft.Web.WebView2 NuGet package does.
			var deferral = args.GetDeferral();
			try
			{
				args.Response = await CreateResponseAsync(coreWebView, normalizedContentRoot, args.Request.Uri);
			}
			finally
			{
				deferral.Complete();
			}
		};

		if (dictionaryLookupRequested is not null)
		{
			AddDictionaryLookupMenuItem(nativeWebView, dictionaryLookupRequested);
		}
	}

	static async Task<CoreWebView2WebResourceResponse> CreateResponseAsync(CoreWebView2 coreWebView, string contentRoot, string requestUri)
	{
		string requestPath = Uri.UnescapeDataString(new Uri(requestUri).AbsolutePath).TrimStart('/');
		string fullPath = Path.GetFullPath(Path.Combine(contentRoot, requestPath.Replace('/', Path.DirectorySeparatorChar)));

		if (!fullPath.StartsWith(contentRoot, StringComparison.Ordinal) || !File.Exists(fullPath))
		{
			return coreWebView.Environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
		}

		byte[] data = await File.ReadAllBytesAsync(fullPath);
		string mimeType = mimeTypesByExtension.TryGetValue(Path.GetExtension(fullPath), out string? type)
			? type
			: "application/octet-stream";

		// The book's files never change once extracted, so it's safe to let WebView2 cache
		// and reuse these responses instead of re-reading them from disk on every request
		// (including the JS side's background preload of every remaining chapter).
		string headers =
			$"Content-Type: {mimeType}\r\n" +
			"Access-Control-Allow-Origin: *\r\n" +
			"Cache-Control: public, max-age=31536000, immutable";
		MemoryStream stream = new(data);
		return coreWebView.Environment.CreateWebResourceResponse(stream.AsRandomAccessStream(), 200, "OK", headers);
	}

	static void AddDictionaryLookupMenuItem(
		Microsoft.UI.Xaml.Controls.WebView2 nativeWebView,
		Action<string> dictionaryLookupRequested)
	{
		nativeWebView.CoreWebView2.ContextMenuRequested += (_, args) =>
		{
			if (args.ContextMenuTarget.Kind != Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuTargetKind.SelectedText)
			{
				return;
			}

			string selection = args.ContextMenuTarget.SelectionText;
			if (string.IsNullOrWhiteSpace(selection))
			{
				return;
			}

			string label = $"Look up “{TruncateForLabel(selection)}”";
			CoreWebView2ContextMenuItem menuItem = nativeWebView.CoreWebView2.Environment.CreateContextMenuItem(
				label,
				null,
				Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuItemKind.Command);
			menuItem.CustomItemSelected += (_, _) => dictionaryLookupRequested(selection);
			args.MenuItems.Insert(0, menuItem);
		};
	}

	static string TruncateForLabel(string selection)
	{
		string trimmed = selection.Trim();
		return trimmed.Length > lookUpMenuLabelMaxLength
			? string.Concat(trimmed.AsSpan(0, lookUpMenuLabelMaxLength), "…")
			: trimmed;
	}

	private static partial Uri CreateViewerUri(string publicationRoot, string opfRelativePath)
	{
		string opf = CreateOpfQuery(publicationRoot, opfRelativePath);
		return new Uri($"https://{hostName}/DisplayBookViewer/index.html?opf={opf}&bridge=displaybook%3A%2F%2Fbridge", UriKind.Absolute);
	}
}
