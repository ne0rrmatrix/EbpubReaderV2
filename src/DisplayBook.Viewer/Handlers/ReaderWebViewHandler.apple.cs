#if IOS || MACCATALYST
using CoreGraphics;
using DisplayBook.Viewer.Services;
using Foundation;
using Microsoft.Maui.Platform;
using WebKit;

namespace DisplayBook.Viewer.Handlers;

public sealed partial class ReaderWebViewHandler : IDisposable
{
	ReaderContentSchemeHandler? contentSchemeHandler;

	public void Dispose()
	{
		contentSchemeHandler?.Dispose();
		contentSchemeHandler = null;
	}

	partial void ConfigurePlatformView(bool isReaderContentHost)
	{
		// WKWebView is configured by ReaderAssetHost after the view is created, so no additional configuration is needed here.
	}

	/// <summary>
	/// Registers a <see cref="ReaderContentSchemeHandler"/> instead of relying on a plain
	/// <c>file://</c> load. WKWebView only grants read access to the folder containing the
	/// loaded page and blocks <c>fetch()</c> to other <c>file://</c> resources, so serving a
	/// publication needs its own scheme regardless of whether the content is disk- or
	/// memory-backed.
	/// </summary>
	protected override WKWebView CreatePlatformView()
	{
		WKWebViewConfiguration configuration = MauiWKWebView.CreateConfiguration();
		contentSchemeHandler = new ReaderContentSchemeHandler();
		configuration.SetUrlSchemeHandler(contentSchemeHandler, ReaderAssetHost.AppleContentScheme);

		return new MauiWKWebView(CGRect.Empty, this, configuration);
	}

	/// <summary>
	/// Points this handler's scheme handler at the currently-open book. Called from
	/// <c>ReaderAssetHost.ios.cs</c>/<c>.maccatalyst.cs</c>'s <c>ConfigurePlatformWebViewAsync</c>
	/// each time <c>LoadPublicationAsync</c> runs, before the WebView navigates to the viewer URI.
	/// </summary>
	internal void SetPublicationSource(EpubArchive publicationSource)
	{
		ReaderContentSchemeHandler handler = contentSchemeHandler
			?? throw new InvalidOperationException("The Apple reader WebView has no content scheme handler yet.");
		handler.PublicationSource = publicationSource;
	}

	sealed class ReaderContentSchemeHandler : NSObject, IWKUrlSchemeHandler
	{
		public EpubArchive? PublicationSource { get; set; }

		public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
		{
			NSUrl? requestUrl = urlSchemeTask.Request.Url;
			string requestedPath = Uri.UnescapeDataString(requestUrl?.Path ?? string.Empty).TrimStart('/');
			EpubArchive? publicationSource = PublicationSource;

			if (requestUrl is null || publicationSource is null ||
				!ReaderAssetHost.TryGetResourceBytes(publicationSource, requestedPath, out byte[] data))
			{
				urlSchemeTask.DidFailWithError(new NSError(new NSString("DisplayBookReaderContent"), 404));
				return;
			}

			try
			{
				string mimeType = ReaderAssetHost.MimeTypesByExtension.TryGetValue(Path.GetExtension(requestedPath), out string? type)
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
