using Android.Webkit;
using AndroidX.WebKit;

namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost
{
	const string assetHost = "https://appassets.androidplatform.net/content/";

	private static partial Task ConfigurePlatformWebViewAsync(
		Microsoft.Maui.Controls.WebView webView,
		EpubArchive publicationSource,
		Func<string, Task>? navigationHandler,
		Action<string>? dictionaryLookupRequested,
		CancellationToken cancellationToken)
	{
		if (webView.Handler?.PlatformView is not DisplayBook.Viewer.Handlers.ReaderSelectionWebView nativeWebView)
		{
			throw new InvalidOperationException("The Android reader WebView is not ready for local content hosting.");
		}

		nativeWebView.LookupRequestedHandler = dictionaryLookupRequested;

		WebViewAssetLoader.Builder assetLoaderBuilder = new();
		assetLoaderBuilder.AddPathHandler("/content/", new InMemoryPathHandler(publicationSource));
		WebViewAssetLoader assetLoader = assetLoaderBuilder.Build() ?? throw new InvalidOperationException("Android could not create the reader asset loader.");
		WebSettings settings = nativeWebView.Settings ?? throw new InvalidOperationException("The Android reader WebView has no settings.");
		settings.JavaScriptEnabled = true;
		nativeWebView.SetWebViewClient(new ReaderAssetWebViewClient(assetLoader, navigationHandler));
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}

	private static partial Uri CreateViewerUri(string opfRelativePath)
	{
		string opf = CreateOpfQuery(opfRelativePath);
		return new Uri($"{assetHost}DisplayBookViewer/index.html?opf={opf}&bridge=displaybook%3A%2F%2Fbridge", UriKind.Absolute);
	}

	private static partial Uri CreateShellUri()
	{
		return new Uri($"{assetHost}DisplayBookViewer/index.html?bridge=displaybook%3A%2F%2Fbridge", UriKind.Absolute);
	}

	/// <summary>
	/// Replaces <c>WebViewAssetLoader.InternalStoragePathHandler</c> (disk-only) with a lookup
	/// against the in-memory <see cref="EpubArchive"/> -- see <see cref="TryGetResourceBytes"/>.
	/// </summary>
	sealed class InMemoryPathHandler(EpubArchive publicationSource) : Java.Lang.Object, WebViewAssetLoader.IPathHandler
	{
		// "new" acknowledges this intentionally shares a name with Java.Lang.Object.Handle (the
		// JNI handle property) -- unrelated members, just a naming collision from the Java
		// interface being called "handle".
		public new WebResourceResponse? Handle(string? path)
		{
			if (path is null || !TryGetResourceBytes(publicationSource, path, out byte[] data))
			{
				return new WebResourceResponse(null, null, 404, "Not Found", null, null);
			}

			string mimeType = MimeTypesByExtension.TryGetValue(Path.GetExtension(path), out string? type)
				? type
				: "application/octet-stream";
			return new WebResourceResponse(mimeType, null, new MemoryStream(data));
		}
	}

	sealed class ReaderAssetWebViewClient(
		AndroidX.WebKit.WebViewAssetLoader assetLoader,
		Func<string, Task>? navigationHandler) : Android.Webkit.WebViewClient
	{
		readonly Lock navigationQueueLock = new();
		Task navigationQueue = Task.CompletedTask;

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

		bool HandleNavigation(string? url)
		{
			if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
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

		void QueueNavigation(string url)
		{
			lock (navigationQueueLock)
			{
				navigationQueue = ProcessNavigationAsync(navigationQueue, url);
			}
		}

		async Task ProcessNavigationAsync(Task previousNavigation, string url)
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
