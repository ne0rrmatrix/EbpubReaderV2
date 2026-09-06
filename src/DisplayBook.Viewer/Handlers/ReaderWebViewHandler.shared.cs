using DisplayBook.Viewer.Controls;
using Microsoft.Maui.Handlers;

namespace DisplayBook.Viewer.Handlers;

/// <summary>
/// Connects <see cref="ReaderWebView"/> to the native WebView used by the host platform.
/// </summary>
public sealed partial class ReaderWebViewHandler : WebViewHandler
{
    public static readonly IPropertyMapper<ReaderWebView, ReaderWebViewHandler> PropertyMapper =
        new PropertyMapper<ReaderWebView, ReaderWebViewHandler>(WebViewHandler.Mapper)
        {
            [nameof(ReaderWebView.IsReaderContentHost)] = MapIsReaderContentHost
        };

    public ReaderWebViewHandler()
        : base(PropertyMapper)
    {
    }

    public static void MapIsReaderContentHost(ReaderWebViewHandler handler, ReaderWebView view)
    {
        handler.ConfigurePlatformView(view.IsReaderContentHost);
    }

    partial void ConfigurePlatformView(bool isReaderContentHost);
}