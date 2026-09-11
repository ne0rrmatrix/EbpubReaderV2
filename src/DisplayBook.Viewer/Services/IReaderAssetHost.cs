namespace DisplayBook.Viewer.Services;

public interface IReaderAssetHost
{
	Task InitializeAsync(
		WebView webView,
		Func<string, Task>? navigationHandler = null,
		Action<string>? dictionaryLookupRequested = null,
		CancellationToken cancellationToken = default);
	Uri GetViewerUri(string publicationRoot, string opfRelativePath);
}