using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace DisplayBook.Viewer.Services;

public sealed partial class ReaderAssetHost
{
	// The TLD here is load-bearing, not cosmetic. Every request to this host is answered from
	// memory by WebResourceRequested below, so the name never needs to resolve -- but Chromium
	// still kicks off a DNS lookup for a navigation's origin, and Windows routes anything under
	// ".local" to multicast DNS, which has no responder here and takes seconds to give up rather
	// than failing fast. That showed up as a flat ~2s stall on every document navigation,
	// independent of payload size (measured: the same 2s to load a few KB of shell assets as to
	// load a 1.1 MB combined document). ".invalid" is reserved by RFC 6761 precisely so that it
	// never resolves, and resolvers reject it immediately. Do not move this back under ".local",
	// and do not point it at a name that could really resolve.
	const string hostName = "displaybook.invalid";

	const int lookUpMenuLabelMaxLength = 24;

	// Keyed by native WebView2 instance (rather than a single static Task) so a handler that
	// gets torn down and recreated -- e.g. navigating away from and back to the reader page --
	// warms up its own new CoreWebView2 instead of awaiting a stale one.
	static readonly ConditionalWeakTable<Microsoft.UI.Xaml.Controls.WebView2, Task> warmUpTasks = new();

	// The WebResourceRequested handler is registered once per native WebView2, during warm-up --
	// well before any book is chosen. This box is the mutable link between that one-time handler
	// and whichever book is currently open, updated by ConfigurePlatformWebViewAsync each time
	// LoadPublicationAsync runs.
	static readonly ConditionalWeakTable<Microsoft.UI.Xaml.Controls.WebView2, PublicationSourceBox> publicationSources = new();

	// Same idea as publicationSources: the reader shell (and therefore its native WebView2) now
	// outlives any single book, so the ContextMenuRequested handler below is wired up once per
	// WebView2 instance (during warm-up) rather than once per book -- re-subscribing on every
	// LoadPublicationAsync call would otherwise pile up one more "Look up..." menu item per book
	// opened in the session.
	static readonly ConditionalWeakTable<Microsoft.UI.Xaml.Controls.WebView2, DictionaryLookupBox> dictionaryLookupHandlers = new();

	// Spinning up the WebView2 runtime's browser process is the single slowest part of getting a
	// CoreWebView2 ready -- and it's shared across every CoreWebView2 in the process, so it only
	// needs to happen once. Kicking it off here (see WarmUpEnvironmentAsync, called from the
	// Windows App constructor -- as early as the process itself starts) lets that spin-up overlap
	// with the rest of app startup instead of blocking the first time a WebView2 control actually
	// needs one.
	static readonly Lazy<Task<CoreWebView2Environment>> sharedEnvironment = new(
		static () => CoreWebView2Environment.CreateAsync().AsTask());

	public static Task<CoreWebView2Environment> WarmUpEnvironmentAsync() => sharedEnvironment.Value;

	private static async partial Task ConfigurePlatformWebViewAsync(
		Microsoft.Maui.Controls.WebView webView,
		EpubArchive publicationSource,
		Func<string, Task>? navigationHandler,
		Action<string>? dictionaryLookupRequested,
		CancellationToken cancellationToken)
	{
		if (webView.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 nativeWebView)
		{
			throw new InvalidOperationException("The Windows reader WebView is not ready for local content hosting.");
		}

		publicationSources.GetValue(nativeWebView, static _ => new PublicationSourceBox()).Current = publicationSource;
		dictionaryLookupHandlers.GetValue(nativeWebView, static _ => new DictionaryLookupBox()).Current = dictionaryLookupRequested;

		// Usually already complete by the time a book is opened: ReaderWebViewHandler kicks
		// this off as soon as the native WebView2 is created (see WarmUpAsync), well before
		// LoadPublicationAsync gets here. Awaiting it again is a no-op in that case and just
		// observes/rethrows any warm-up failure; it only actually waits if this is called
		// unusually fast after the handler was created.
		await WarmUpAsync(nativeWebView);
		cancellationToken.ThrowIfCancellationRequested();
	}

	/// <summary>
	/// Ensures CoreWebView2 is initialized and in-memory content hosting is wired up for
	/// <paramref name="nativeWebView"/>, starting that work the first time it's called for a
	/// given instance and simply returning the same task on every subsequent call. Called from
	/// <c>ReaderWebViewHandler</c>'s <c>Loaded</c> handler as soon as the native WebView2 loads,
	/// and from <see cref="ConfigurePlatformWebViewAsync"/> above, which awaits the same task
	/// once a book is actually opened.
	/// </summary>
	internal static Task WarmUpAsync(Microsoft.UI.Xaml.Controls.WebView2 nativeWebView) =>
		warmUpTasks.GetValue(nativeWebView, static webView => InitializeCoreWebView2Async(webView));

	static async Task InitializeCoreWebView2Async(Microsoft.UI.Xaml.Controls.WebView2 nativeWebView)
	{
		// Only spin up the shared browser process here -- deliberately not passing this
		// environment object into EnsureCoreWebView2Async below. WebView2 throws if a control is
		// ever initialized with a different environment instance than whatever first initialized
		// it (including one MAUI's own WebView2 plumbing might create implicitly), so forcing this
		// one in is a real risk for a benefit (skipping a small per-control init step) that's
		// already dwarfed by the shared browser-process spin-up this await captures anyway.
		await WarmUpEnvironmentAsync();
		await nativeWebView.EnsureCoreWebView2Async();

		// The old disk-backed version of this handler read each requested file with
		// File.ReadAllBytesAsync -- a real OS file-open per chapter/CSS/image/font, subject to
		// real-time antivirus scanning, repeated for every book (EpubText.js background-preloads
		// every remaining chapter right after a book opens). Content now comes from the
		// already-in-memory EpubArchive (see PublicationSourceBox below), so responding is a
		// synchronous dictionary lookup -- no deferral needed at all.
		PublicationSourceBox sourceBox = publicationSources.GetValue(nativeWebView, static _ => new PublicationSourceBox());
		CoreWebView2 coreWebView = nativeWebView.CoreWebView2;
		coreWebView.AddWebResourceRequestedFilter($"https://{hostName}/*", CoreWebView2WebResourceContext.All);
		coreWebView.WebResourceRequested += (_, args) =>
		{
			args.Response = CreateResponse(coreWebView, sourceBox, args.Request.Uri);
		};

		DictionaryLookupBox lookupBox = dictionaryLookupHandlers.GetValue(nativeWebView, static _ => new DictionaryLookupBox());
		AddDictionaryLookupMenuItem(nativeWebView, lookupBox);
	}

	static CoreWebView2WebResourceResponse CreateResponse(CoreWebView2 coreWebView, PublicationSourceBox sourceBox, string requestUri)
	{
		string requestPath = Uri.UnescapeDataString(new Uri(requestUri).AbsolutePath).TrimStart('/');
		EpubArchive? publicationSource = sourceBox.Current;
		if (publicationSource is null || !TryGetResourceBytes(publicationSource, requestPath, out byte[] data))
		{
			return coreWebView.Environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
		}

		string mimeType = MimeTypesByExtension.TryGetValue(Path.GetExtension(requestPath), out string? type)
			? type
			: "application/octet-stream";

		// The static viewer shell (index.html/EpubText.js/CSS) never changes at runtime, so it's
		// safe to let WebView2 cache it aggressively. Book content, on the other hand, now
		// outlives a single WebView2 navigation -- the reader shell stays loaded and later books
		// are swapped in via loadPublication (see EpubReaderView) rather than a fresh page load --
		// and two different books can easily share the same internal relative paths (e.g. both
		// "OEBPS/content.opf"). An aggressive/immutable cache would then risk serving book A's
        // bytes back for book B's identical-looking URL, so book content is never cached; it's
		// already just an in-memory dictionary lookup; no perf is lost by not caching it.
		bool isShellAsset = requestPath.StartsWith("DisplayBookViewer/", StringComparison.OrdinalIgnoreCase);
		string cacheControl = isShellAsset ? "public, max-age=31536000, immutable" : "no-store";
		string headers =
			$"Content-Type: {mimeType}\r\n" +
			"Access-Control-Allow-Origin: *\r\n" +
			$"Cache-Control: {cacheControl}";
		MemoryStream stream = new(data);
		return coreWebView.Environment.CreateWebResourceResponse(stream.AsRandomAccessStream(), 200, "OK", headers);
	}

	static void AddDictionaryLookupMenuItem(
		Microsoft.UI.Xaml.Controls.WebView2 nativeWebView,
		DictionaryLookupBox lookupBox)
	{
		nativeWebView.CoreWebView2.ContextMenuRequested += (_, args) =>
		{
			if (lookupBox.Current is not { } dictionaryLookupRequested)
			{
				return;
			}

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

	private static partial Uri CreateViewerUri(string opfRelativePath)
	{
		string opf = CreateOpfQuery(opfRelativePath);
		return new Uri($"https://{hostName}/DisplayBookViewer/index.html?opf={opf}&bridge=displaybook%3A%2F%2Fbridge", UriKind.Absolute);
	}

	private static partial Uri CreateShellUri()
	{
		return new Uri($"https://{hostName}/DisplayBookViewer/index.html?bridge=displaybook%3A%2F%2Fbridge", UriKind.Absolute);
	}

	sealed class PublicationSourceBox
	{
		public EpubArchive? Current;
	}

	sealed class DictionaryLookupBox
	{
		public Action<string>? Current;
	}
}
