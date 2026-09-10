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
        var configuration = MauiWKWebView.CreateConfiguration();
        configuration.SetUrlSchemeHandler(
            new ReaderContentSchemeHandler(ReaderAssetHost.ContentRoot),
            ReaderAssetHost.AppleContentScheme);

        return new MauiWKWebView(CGRect.Empty, this, configuration);
    }

    private sealed class ReaderContentSchemeHandler(string contentRoot) : NSObject, IWKUrlSchemeHandler
    {
        private static readonly Dictionary<string, string> MimeTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
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

        private readonly string _contentRoot = Path.GetFullPath(contentRoot);

        public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
        {
            var requestUrl = urlSchemeTask.Request.Url;
            var requestedPath = Uri.UnescapeDataString(requestUrl?.Path ?? string.Empty).TrimStart('/');
            var fullPath = Path.GetFullPath(
                Path.Combine(_contentRoot, requestedPath.Replace('/', Path.DirectorySeparatorChar)));

            if (requestUrl is null ||
                !fullPath.StartsWith(_contentRoot, StringComparison.Ordinal) ||
                !File.Exists(fullPath))
            {
                urlSchemeTask.DidFailWithError(new NSError(new NSString("DisplayBookReaderContent"), 404));
                return;
            }

            try
            {
                var data = File.ReadAllBytes(fullPath);
                var mimeType = MimeTypesByExtension.TryGetValue(Path.GetExtension(fullPath), out var type)
                    ? type
                    : "application/octet-stream";

                // fetch() reports response.status/ok from an HTTP-style response. A plain
                // NSUrlResponse has no status code, which WebKit surfaces to fetch() as status 0
                // (treated as a failed load), even though <script>/<link> tag loads succeed with it.
                var headers = new NSMutableDictionary<NSString, NSString>
                {
                    [(NSString)"Content-Type"] = (NSString)mimeType,
                    [(NSString)"Content-Length"] = (NSString)data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
                using var response = new NSHttpUrlResponse(requestUrl, 200, "HTTP/1.1", headers);
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
