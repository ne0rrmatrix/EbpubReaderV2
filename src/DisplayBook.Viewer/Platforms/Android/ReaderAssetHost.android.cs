namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost
{
    private const string AssetHost = "https://appassets.androidplatform.net/content/";

    private static partial Task ConfigurePlatformWebViewAsync(
        Microsoft.Maui.Controls.WebView webView,
        string contentRoot,
        Func<string, Task>? navigationHandler,
        Action<string>? dictionaryLookupRequested,
        CancellationToken cancellationToken)
    {
        if (webView.Handler?.PlatformView is not DisplayBook.Viewer.Handlers.ReaderSelectionWebView nativeWebView)
        {
            throw new InvalidOperationException("The Android reader WebView is not ready for local content hosting.");
        }

        if (dictionaryLookupRequested is not null)
        {
            nativeWebView.SelectionLookupRequested += (_, selection) => dictionaryLookupRequested(selection);
        }

        var context = nativeWebView.Context ?? throw new InvalidOperationException("The Android reader WebView has no context.");
        var filesDirectory = context.FilesDir?.AbsolutePath;
        if (string.IsNullOrWhiteSpace(filesDirectory) || !filesDirectory.StartsWith('/'))
        {
            throw new InvalidOperationException("Android returned an invalid application files directory.");
        }

        var androidContentRoot = Path.Combine(filesDirectory, "ReaderContent");
        if (!string.Equals(Path.GetFullPath(contentRoot), Path.GetFullPath(androidContentRoot), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The reader content root does not match Android application storage.");
        }

        var assetLoaderBuilder = new AndroidX.WebKit.WebViewAssetLoader.Builder();
        assetLoaderBuilder.AddPathHandler(
            "/content/",
            new AndroidX.WebKit.WebViewAssetLoader.InternalStoragePathHandler(context, new Java.IO.File(androidContentRoot)));
        var assetLoader = assetLoaderBuilder.Build() ?? throw new InvalidOperationException("Android could not create the reader asset loader.");
        var settings = nativeWebView.Settings ?? throw new InvalidOperationException("The Android reader WebView has no settings.");
        settings.JavaScriptEnabled = true;
        nativeWebView.SetWebViewClient(new ReaderAssetWebViewClient(assetLoader, navigationHandler));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static partial Uri CreateViewerUri(string publicationRoot, string opfRelativePath)
    {
        var opf = CreateOpfQuery(publicationRoot, opfRelativePath);
        return new Uri($"{AssetHost}DisplayBookViewer/index.html?opf={opf}&bridge=displaybook%3A%2F%2Fbridge", UriKind.Absolute);
    }

    private sealed class ReaderAssetWebViewClient(
        AndroidX.WebKit.WebViewAssetLoader assetLoader,
        Func<string, Task>? navigationHandler) : Android.Webkit.WebViewClient
    {
        private readonly object _navigationQueueLock = new();
        private Task _navigationQueue = Task.CompletedTask;

        public override bool ShouldOverrideUrlLoading(Android.Webkit.WebView? view, Android.Webkit.IWebResourceRequest? request)
        {
            return HandleNavigation(request?.Url?.ToString());
        }

        public override bool ShouldOverrideUrlLoading(Android.Webkit.WebView? view, string? url)
        {
            return HandleNavigation(url);
        }

        public override Android.Webkit.WebResourceResponse? ShouldInterceptRequest(
            Android.Webkit.WebView? view,
            Android.Webkit.IWebResourceRequest? request)
        {
            return request?.Url is null ? null : assetLoader.ShouldInterceptRequest(request.Url);
        }

        private bool HandleNavigation(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "displaybook", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "bridge", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (navigationHandler is not null)
            {
                QueueNavigation(uri.ToString());
            }

            return true;
        }

        private void QueueNavigation(string url)
        {
            lock (_navigationQueueLock)
            {
                _navigationQueue = ProcessNavigationAsync(_navigationQueue, url);
            }
        }

        private async Task ProcessNavigationAsync(Task previousNavigation, string url)
        {
            try
            {
                await previousNavigation;
            }
            catch (Exception exception)
            {
                Android.Util.Log.Error(nameof(ReaderAssetWebViewClient), $"Reader bridge navigation queue failed: {exception}");
            }

            try
            {
                if (navigationHandler is not null)
                {
                    await navigationHandler(url);
                }
            }
            catch (Exception exception)
            {
                Android.Util.Log.Error(nameof(ReaderAssetWebViewClient), $"Reader bridge navigation failed: {exception}");
            }
        }
    }
}
