using System.Runtime.Versioning;
using DisplayBook.Viewer.Controls;
using DisplayBook.Viewer.Handlers;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Storage;

namespace DisplayBook.Viewer;

/// <summary>
/// Provides the application-builder extensions required by the DisplayBook viewer library.
/// </summary>
[SupportedOSPlatform("Android21.0")]
[SupportedOSPlatform("Windows10.0.17763")]
[SupportedOSPlatform("iOS15.0")]
[SupportedOSPlatform("MacCatalyst15.0")]
public static class AppBuilderExtensions
{
    /// <summary>
    /// Registers the DisplayBook reader control and its platform handlers.
    /// </summary>
    public static MauiAppBuilder UseDisplayBookViewer(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureMauiHandlers(handlers =>
        {
            handlers.AddHandler<ReaderWebView, ReaderWebViewHandler>();
        });

#if WINDOWS
        var webViewDataDirectory = Path.Combine(FileSystem.AppDataDirectory, "DisplayBookWebView2");
        Directory.CreateDirectory(webViewDataDirectory);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewDataDirectory);
#endif

        return builder;
    }
}