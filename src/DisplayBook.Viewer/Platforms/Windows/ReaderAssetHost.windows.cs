namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost
{
    private const string HostName = "displaybook.local";

    private static async partial Task ConfigurePlatformWebViewAsync(
        Microsoft.Maui.Controls.WebView webView,
        string contentRoot,
        Func<string, Task>? navigationHandler,
        CancellationToken cancellationToken)
    {
        if (webView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 nativeWebView)
        {
            throw new InvalidOperationException("The Windows reader WebView is not ready for local content hosting.");
        }

        await nativeWebView.EnsureCoreWebView2Async();
        cancellationToken.ThrowIfCancellationRequested();
        nativeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            HostName,
            contentRoot,
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
    }

    private static partial Uri CreateViewerUri(string publicationRoot, string opfRelativePath)
    {
        var opf = CreateOpfQuery(publicationRoot, opfRelativePath);
        return new Uri($"https://{HostName}/DisplayBookViewer/index.html?opf={opf}&bridge=displaybook%3A%2F%2Fbridge", UriKind.Absolute);
    }
}
