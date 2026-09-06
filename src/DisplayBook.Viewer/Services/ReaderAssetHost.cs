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
    private bool _initialized;

    public async Task InitializeAsync(
        WebView webView,
        Func<string, Task>? navigationHandler = null,
        CancellationToken cancellationToken = default)
    {
        await InitializationLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ContentRoot);
            if (!_initialized)
            {
                foreach (var assetPath in ViewerAssetPaths)
                {
                    var destination = Path.Combine(ContentRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                    await using var source = await FileSystem.OpenAppPackageFileAsync(assetPath);
                    await using var target = File.Create(destination);
                    await source.CopyToAsync(target, cancellationToken);
                }

                _initialized = true;
            }

            await ConfigurePlatformWebViewAsync(webView, ContentRoot, navigationHandler, cancellationToken);
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

    private static string ContentRoot => Path.Combine(FileSystem.AppDataDirectory, "ReaderContent");

    private static partial Task ConfigurePlatformWebViewAsync(
        WebView webView,
        string contentRoot,
        Func<string, Task>? navigationHandler,
        CancellationToken cancellationToken);
    private static partial Uri CreateViewerUri(string publicationRoot, string opfRelativePath);
}
