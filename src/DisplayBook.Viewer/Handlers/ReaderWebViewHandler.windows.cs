#if WINDOWS
using DisplayBook.Viewer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DisplayBook.Viewer.Handlers;

public sealed partial class ReaderWebViewHandler
{
	partial void ConfigurePlatformView(bool isReaderContentHost)
	{
		// Windows WebView2 is warmed up once Loaded fires below; the rest of the per-book
		// setup happens in ReaderAssetHost once CoreWebView2 is ready.
	}

	/// <summary>
	/// Starts warming up CoreWebView2 (see <see cref="ReaderAssetHost.WarmUpAsync"/>) as soon as
	/// the native WebView2 loads, instead of waiting for <c>ConfigurePlatformWebViewAsync</c> to
	/// run when a book is actually opened. Spinning up the WebView2 runtime process is the
	/// slowest part of the first load, so starting it here lets it run concurrently with EPUB
	/// extraction/parsing rather than blocking on it.
	/// </summary>
	protected override WebView2 CreatePlatformView()
	{
		WebView2 platformView = base.CreatePlatformView();
		platformView.Loaded += OnPlatformViewLoaded;
		return platformView;
	}

	// CreatePlatformView can't be async (it overrides a synchronous base signature), and
	// warm-up can't be started with a discarded `_ = WarmUpAsync(...)` either -- that would
	// leave its exception unobserved unless a book happens to get opened afterward. Loaded
	// is a genuine event, so handling it with async void here is the one place that's
	// legitimate: a failure surfaces as an unhandled exception instead of being swallowed.
	static async void OnPlatformViewLoaded(object sender, RoutedEventArgs e)
	{
		WebView2 platformView = (WebView2)sender;
		platformView.Loaded -= OnPlatformViewLoaded;
		await ReaderAssetHost.WarmUpAsync(platformView);
	}
}
#endif
