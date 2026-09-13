namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost
{
	private static partial Task ConfigurePlatformWebViewAsync(
		Microsoft.Maui.Controls.WebView webView,
		string contentRoot,
		Func<string, Task>? navigationHandler,
		Action<string>? dictionaryLookupRequested,
		CancellationToken cancellationToken)
	{
		if (webView.Handler?.PlatformView is null)
		{
			throw new InvalidOperationException("The Apple reader WebView is not ready for local content hosting.");
		}

		cancellationToken.ThrowIfCancellationRequested();
		return Task.CompletedTask;
	}

	private static partial Uri CreateViewerUri(string publicationRoot, string opfRelativePath)
	{
		string opf = CreateOpfQuery(publicationRoot, opfRelativePath);
		return new Uri(
			$"{AppleContentScheme}://{AppleContentHost}/DisplayBookViewer/index.html?opf={opf}&bridge=displaybook%3A%2F%2Fbridge",
			UriKind.Absolute);
	}
}