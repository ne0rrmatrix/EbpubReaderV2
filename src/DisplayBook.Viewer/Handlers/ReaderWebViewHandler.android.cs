#if ANDROID
using Android.Content.PM;
using Android.Views;
using Android.Webkit;
using LayoutParams = Android.Views.ViewGroup.LayoutParams;

namespace DisplayBook.Viewer.Handlers;

public sealed partial class ReaderWebViewHandler
{
	partial void ConfigurePlatformView(bool isReaderContentHost)
	{
		WebSettings settings = PlatformView.Settings;
		settings.JavaScriptEnabled = isReaderContentHost;
		settings.SetSupportMultipleWindows(false);
	}

	/// <summary>
	/// Mirrors <c>WebViewHandler.CreatePlatformView()</c> but produces a
	/// <see cref="ReaderSelectionWebView"/> so the reader's text-selection toolbar is
	/// app-owned instead of the OS default.
	/// </summary>
	protected override Android.Webkit.WebView CreatePlatformView()
	{
#if DEBUG
		Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true);
#endif

		ReaderSelectionWebView platformView = new(this, Context!)
		{
			LayoutParameters = new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent)
		};

		WebSettings settings = platformView.Settings;
		settings.JavaScriptEnabled = true;
		settings.DomStorageEnabled = true;
		settings.SetSupportMultipleWindows(true);
		settings.SetSupportZoom(false);
		settings.BuiltInZoomControls = false;
		settings.UseWideViewPort = true;

		if (OperatingSystem.IsAndroidVersionAtLeast(23) &&
			Context?.ApplicationInfo?.Flags.HasFlag(ApplicationInfoFlags.HardwareAccelerated) == false)
		{
			platformView.SetLayerType(LayerType.Software, null);
		}

		// ReaderPage/EpubReaderView opt out of MAUI's safe-area handling
		// (SafeAreaEdges="None") so the reader's themed background can draw truly
		// edge-to-edge behind the status bar/notch and navigation bar. Unlike
		// WKWebView on iOS, Android WebView never resolves CSS
		// env(safe-area-inset-*), so without this the paginated column layout has
		// no way to know those regions are occluded -- a line of text can land
		// underneath an opaque system bar, invisible, while pagination's page-count
		// math already considers it "shown" and never repeats it on the next page.
		// GlobalLayout (not a one-shot) re-measures on rotation and on system bar/
		// gesture-nav visibility changes, pushing updated insets to the JS side via
		// EpubText.js's setSafeAreaInsets, which only re-measures pagination when
		// the values actually changed.
		platformView.ViewTreeObserver?.GlobalLayout += (_, _) => PushSafeAreaInsets(platformView);

		return platformView;
	}

	static void PushSafeAreaInsets(Android.Webkit.WebView platformView)
	{
		Android.Views.WindowInsets? insets = platformView.RootWindowInsets;
		if (insets is null)
		{
			return;
		}

		float density = platformView.Resources?.DisplayMetrics?.Density ?? 1f;
		if (density <= 0f)
		{
			return;
		}

		int topPx;
		int bottomPx;
		if (OperatingSystem.IsAndroidVersionAtLeast(30))
		{
			Android.Graphics.Insets systemBarInsets = insets.GetInsets(
				Android.Views.WindowInsets.Type.SystemBars() | Android.Views.WindowInsets.Type.DisplayCutout());
			topPx = systemBarInsets.Top;
			bottomPx = systemBarInsets.Bottom;
		}
		else
		{
#pragma warning disable CS0618 // StableInset* is deprecated in favor of GetInsets(int), which needs API 30.
			topPx = insets.StableInsetTop;
			bottomPx = insets.StableInsetBottom;
#pragma warning restore CS0618
		}

		double topDp = topPx / density;
		double bottomDp = bottomPx / density;
		string script = FormattableString.Invariant(
			$"window.DisplayBookReader?.setSafeAreaInsets({topDp}, {bottomDp});");
		platformView.EvaluateJavascript(script, null);
	}
}
#endif