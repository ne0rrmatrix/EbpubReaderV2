#if ANDROID
using Android.Content.PM;
using Android.Views;
using LayoutParams = Android.Views.ViewGroup.LayoutParams;

namespace DisplayBook.Viewer.Handlers;

public sealed partial class ReaderWebViewHandler
{
    partial void ConfigurePlatformView(bool isReaderContentHost)
    {
        var settings = PlatformView.Settings;
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
        var platformView = new ReaderSelectionWebView(this, Context!)
        {
            LayoutParameters = new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent)
        };

        var settings = platformView.Settings;
        settings.JavaScriptEnabled = true;
        settings.DomStorageEnabled = true;
        settings.SetSupportMultipleWindows(true);

        if (OperatingSystem.IsAndroidVersionAtLeast(23) &&
            Context?.ApplicationInfo?.Flags.HasFlag(ApplicationInfoFlags.HardwareAccelerated) == false)
        {
            platformView.SetLayerType(LayerType.Software, null);
        }

        return platformView;
    }
}
#endif