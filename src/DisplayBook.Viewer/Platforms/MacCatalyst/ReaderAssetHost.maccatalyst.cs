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

		if (navigationHandler is not null)
		{
			webView.Navigating += (_, args) =>
			{
				if (!Uri.TryCreate(args.Url, UriKind.Absolute, out Uri? uri) ||
					!string.Equals(uri.Scheme, "displaybook", StringComparison.OrdinalIgnoreCase) ||
					!string.Equals(uri.Host, "bridge", StringComparison.OrdinalIgnoreCase))
				{
					return;
				}

				args.Cancel = true;
			};
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