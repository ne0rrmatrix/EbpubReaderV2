#if IOS || MACCATALYST
using CoreGraphics;
using DisplayBook.Viewer.Services;
using Foundation;
using Microsoft.Maui.Platform;
using WebKit;

namespace DisplayBook.Viewer.Handlers;

public sealed partial class ReaderWebViewHandler
{
	partial void ConfigurePlatformView(bool isReaderContentHost)
	{
		// WKWebView is configured by ReaderAssetHost after the view is created, so no additional configuration is needed here.
	}

	/// <summary>
	/// Registers a <see cref="ReaderContentSchemeHandler"/> instead of relying on a plain
	/// <c>file://</c> load. WKWebView only grants read access to the folder containing the
	/// loaded page and blocks <c>fetch()</c> to other <c>file://</c> resources, so a
	/// publication stored in a sibling folder under <see cref="ReaderAssetHost.ContentRoot"/>
	/// would otherwise fail to load.
	/// </summary>
	protected override WKWebView CreatePlatformView()
	{
		WKWebViewConfiguration configuration = MauiWKWebView.CreateConfiguration();
		configuration.SetUrlSchemeHandler(
			new ReaderContentSchemeHandler(ReaderAssetHost.ContentRoot),
			ReaderAssetHost.AppleContentScheme);

		return new MauiWKWebView(CGRect.Empty, this, configuration);
	}

	sealed class ReaderContentSchemeHandler(string contentRoot) : NSObject, IWKUrlSchemeHandler
	{
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

		readonly string contentRoot = Path.GetFullPath(contentRoot);

		public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
		{
			NSUrl? requestUrl = urlSchemeTask.Request.Url;
			string requestedPath = Uri.UnescapeDataString(requestUrl?.Path ?? string.Empty).TrimStart('/');
			string fullPath = Path.GetFullPath(
				Path.Combine(contentRoot, requestedPath.Replace('/', Path.DirectorySeparatorChar)));

			if (requestUrl is null ||
				!fullPath.StartsWith(contentRoot, StringComparison.Ordinal) ||
				!File.Exists(fullPath))
			{
				urlSchemeTask.DidFailWithError(new NSError(new NSString("DisplayBookReaderContent"), 404));
				return;
			}

			try
			{
				byte[] data = File.ReadAllBytes(fullPath);
				string mimeType = mimeTypesByExtension.TryGetValue(Path.GetExtension(fullPath), out string? type)
					? type
					: "application/octet-stream";

				// fetch() reports response.status/ok from an HTTP-style response. A plain
				// NSUrlResponse has no status code, which WebKit surfaces to fetch() as status 0
				// (treated as a failed load), even though <script>/<link> tag loads succeed with it.
				NSMutableDictionary<NSString, NSString> headers = new()
				{
					[(NSString)"Content-Type"] = (NSString)mimeType,
					[(NSString)"Content-Length"] = (NSString)data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
				};
				using NSHttpUrlResponse response = new(requestUrl, 200, "HTTP/1.1", headers);
				urlSchemeTask.DidReceiveResponse(response);
				urlSchemeTask.DidReceiveData(NSData.FromArray(data));
				urlSchemeTask.DidFinish();
			}
			catch (Exception)
			{
				urlSchemeTask.DidFailWithError(new NSError(new NSString("DisplayBookReaderContent"), 500));
			}
		}

		public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
		{
		}
	}
}
#endif