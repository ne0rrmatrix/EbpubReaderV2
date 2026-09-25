namespace DisplayBook.Viewer.Services;

public interface IReaderAssetHost
{
	Task InitializeAsync(
		WebView webView,
		EpubArchive publicationSource,
		Func<string, Task>? navigationHandler = null,
		Action<string>? dictionaryLookupRequested = null,
		CancellationToken cancellationToken = default);
	Uri GetViewerUri(string opfRelativePath);
	Uri GetShellUri();
}