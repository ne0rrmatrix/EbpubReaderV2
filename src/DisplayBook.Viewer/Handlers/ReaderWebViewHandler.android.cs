#if ANDROID
namespace DisplayBook.Viewer.Handlers;

public sealed partial class ReaderWebViewHandler
{
    partial void ConfigurePlatformView(bool isReaderContentHost)
    {
        var settings = PlatformView.Settings;
        settings.JavaScriptEnabled = isReaderContentHost;
        settings.SetSupportMultipleWindows(false);
    }
}
#endif