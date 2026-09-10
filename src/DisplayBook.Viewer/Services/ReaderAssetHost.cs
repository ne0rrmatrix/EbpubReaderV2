using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost : IReaderAssetHost
{
    private static readonly string[] ViewerAssetPaths =
    [
        "DisplayBookViewer/index.html",
        "DisplayBookViewer/EpubText.js",
        "DisplayBookViewer/EpubText.css",
        "DisplayBookViewer/ReadiumCSS-before.css",
        "DisplayBookViewer/ReadiumCSS-default.css",
        "DisplayBookViewer/ReadiumCSS-after.css"
    ];
    private static readonly SemaphoreSlim InitializationLock = new(1, 1);

    public async Task InitializeAsync(
        WebView webView,
        Func<string, Task>? navigationHandler = null,
        Action<string>? dictionaryLookupRequested = null,
        CancellationToken cancellationToken = default)
    {
        await InitializationLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ContentRoot);
            foreach (var assetPath in ViewerAssetPaths)
            {
                var destination = Path.Combine(ContentRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                await using var source = await FileSystem.OpenAppPackageFileAsync(assetPath);
                await using var target = File.Create(destination);
                await source.CopyToAsync(target, cancellationToken);
            }

            await ConfigurePlatformWebViewAsync(webView, ContentRoot, navigationHandler, dictionaryLookupRequested, cancellationToken);
        }
        finally
        {
            InitializationLock.Release();
        }
    }

    public Uri GetViewerUri(string publicationRoot, string opfRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(opfRelativePath);
        return CreateViewerUri(publicationRoot, opfRelativePath);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static string CreateOpfQuery(string publicationRoot, string opfRelativePath)
    {
        var relativeOpfPath = $"../{NormalizeRelativePath(publicationRoot)}/{NormalizeRelativePath(opfRelativePath)}";
        return Uri.EscapeDataString(relativeOpfPath);
    }

    internal static string ContentRoot => Path.Combine(FileSystem.AppDataDirectory, "ReaderContent");

    /// <summary>
    /// Custom WKWebView URL scheme used to serve reader content on iOS/MacCatalyst.
    /// A plain <c>file://</c> load only grants WKWebView read access to the folder
    /// containing the loaded page, which blocks <c>fetch()</c> calls to publication
    /// files stored in a sibling folder under <see cref="ContentRoot"/>.
    /// </summary>
    internal const string AppleContentScheme = "displaybookcontent";
    internal const string AppleContentHost = "local";

    private static partial Task ConfigurePlatformWebViewAsync(
        WebView webView,
        string contentRoot,
        Func<string, Task>? navigationHandler,
        Action<string>? dictionaryLookupRequested,
        CancellationToken cancellationToken);
    private static partial Uri CreateViewerUri(string publicationRoot, string opfRelativePath);
}
