using Microsoft.Web.WebView2.Core;

namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost
{
	const string hostName = "displaybook.local";

	const int lookUpMenuLabelMaxLength = 24;

	private static async partial Task ConfigurePlatformWebViewAsync(
		Microsoft.Maui.Controls.WebView webView,
		string contentRoot,
		Func<string, Task>? navigationHandler,
		Action<string>? dictionaryLookupRequested,
		CancellationToken cancellationToken)
	{
		if (webView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 nativeWebView)
		{
			throw new InvalidOperationException("The Windows reader WebView is not ready for local content hosting.");
		}

		await nativeWebView.EnsureCoreWebView2Async();
		cancellationToken.ThrowIfCancellationRequested();
		nativeWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
			hostName,
			contentRoot,
			Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

		if (dictionaryLookupRequested is not null)
		{
			AddDictionaryLookupMenuItem(nativeWebView, dictionaryLookupRequested);
		}
	}

	static void AddDictionaryLookupMenuItem(
		Microsoft.UI.Xaml.Controls.WebView2 nativeWebView,
		Action<string> dictionaryLookupRequested)
	{
		nativeWebView.CoreWebView2.ContextMenuRequested += (_, args) =>
		{
			if (args.ContextMenuTarget.Kind != Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuTargetKind.SelectedText)
			{
				return;
			}

			string selection = args.ContextMenuTarget.SelectionText;
			if (string.IsNullOrWhiteSpace(selection))
			{
				return;
			}

			string label = $"Look up “{TruncateForLabel(selection)}”";
			CoreWebView2ContextMenuItem menuItem = nativeWebView.CoreWebView2.Environment.CreateContextMenuItem(
				label,
				null,
				Microsoft.Web.WebView2.Core.CoreWebView2ContextMenuItemKind.Command);
			menuItem.CustomItemSelected += (_, _) => dictionaryLookupRequested(selection);
			args.MenuItems.Insert(0, menuItem);
		};
	}

	static string TruncateForLabel(string selection)
	{
		string trimmed = selection.Trim();
		return trimmed.Length > lookUpMenuLabelMaxLength
			? string.Concat(trimmed.AsSpan(0, lookUpMenuLabelMaxLength), "…")
			: trimmed;
	}

	private static partial Uri CreateViewerUri(string publicationRoot, string opfRelativePath)
	{
		string opf = CreateOpfQuery(publicationRoot, opfRelativePath);
		return new Uri($"https://{hostName}/DisplayBookViewer/index.html?opf={opf}&bridge=displaybook%3A%2F%2Fbridge", UriKind.Absolute);
	}
}