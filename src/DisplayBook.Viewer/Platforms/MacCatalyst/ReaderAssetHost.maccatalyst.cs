using DisplayBook.Viewer.Handlers;

namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost
{
	private static partial Task ConfigurePlatformWebViewAsync(
		Microsoft.Maui.Controls.WebView webView,
		EpubArchive publicationSource,
		Func<string, Task>? navigationHandler,
		Action<string>? dictionaryLookupRequested,
		CancellationToken cancellationToken)
	{
		if (webView.Handler is not ReaderWebViewHandler handler || handler.PlatformView is null)
		{
			throw new InvalidOperationException("The Apple reader WebView is not ready for local content hosting.");
		}

		handler.SetPublicationSource(publicationSource);
		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}

	private static partial Uri CreateViewerUri(string opfRelativePath)
	{
		string opf = CreateOpfQuery(opfRelativePath);
		return new Uri(
			$"{AppleContentScheme}://{AppleContentHost}/DisplayBookViewer/index.html?opf={opf}&bridge=displaybook%3A%2F%2Fbridge",
			UriKind.Absolute);
	}

	private static partial Uri CreateShellUri()
	{
		return new Uri(
			$"{AppleContentScheme}://{AppleContentHost}/DisplayBookViewer/index.html?bridge=displaybook%3A%2F%2Fbridge",
			UriKind.Absolute);
	}
}
