#if WINDOWS
namespace DisplayBook.Viewer.Handlers;

public sealed partial class ReaderWebViewHandler
{
    partial void ConfigurePlatformView(bool isReaderContentHost)
    {
        // Windows WebView2 is configured by ReaderAssetHost after CoreWebView2 is ready.
    }
}
#endif