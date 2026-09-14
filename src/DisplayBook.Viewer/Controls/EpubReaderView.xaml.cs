using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using DisplayBook.Viewer.Bridge;
using DisplayBook.Viewer.Models;
using DisplayBook.Viewer.Serialization;
using DisplayBook.Viewer.Services;

namespace DisplayBook.Viewer.Controls;

public partial class EpubReaderView : ContentView
{
	readonly ReaderAssetHost assetHost = new();
	readonly IDictionaryLookupService dictionaryLookupService = new DictionaryLookupService();
	bool hasLoadedPublication;
	bool isLoading;
	bool isReadyForLocationChanges;
	bool readerReadyReceived;
	bool reloadDispatchQueued;
	EpubLocator? pendingStartLocator;

	// The reader shell (index.html/EpubText.js) is navigated to at most once per WebView2/WKWebView/
	// Android WebView instance -- not once per book. Once isShellReady is true, opening a
	// different book calls window.DisplayBookReader.loadPublication(...) on the already-running
	// JS instead of a fresh navigation, so the WebView/JS engine warm-up and shell asset parsing
	// only ever happens once per app session.
	bool isShellReady;
	TaskCompletionSource<bool>? shellReadyTcs;

	public static readonly BindableProperty PublicationSourceProperty = BindableProperty.Create(
		nameof(PublicationSource),
		typeof(EpubArchive),
		typeof(EpubReaderView),
		default(EpubArchive),
		propertyChanged: OnPublicationChanged);

	public static readonly BindableProperty PublicationOpfPathProperty = BindableProperty.Create(
		nameof(PublicationOpfPath),
		typeof(string),
		typeof(EpubReaderView),
		string.Empty,
		propertyChanged: OnPublicationChanged);

	public static readonly BindableProperty StartLocatorProperty = BindableProperty.Create(
		nameof(StartLocator),
		typeof(EpubLocator),
		typeof(EpubReaderView),
		EpubLocator.Empty);

	public static readonly BindableProperty CoverImageSourceProperty = BindableProperty.Create(
		nameof(CoverImageSource),
		typeof(ImageSource),
		typeof(EpubReaderView),
		default(ImageSource),
		propertyChanged: OnCoverImageSourceChanged);

	public EpubReaderView()
	{
		InitializeComponent();
		Loaded += OnLoaded;
		ReaderWebView.HandlerChanged += OnReaderWebViewHandlerChanged;
	}

	public EpubArchive? PublicationSource
	{
		get => (EpubArchive?)GetValue(PublicationSourceProperty);
		set => SetValue(PublicationSourceProperty, value);
	}

	public string PublicationOpfPath
	{
		get => (string)GetValue(PublicationOpfPathProperty);
		set => SetValue(PublicationOpfPathProperty, value);
	}

	public EpubLocator StartLocator
	{
		get => (EpubLocator)GetValue(StartLocatorProperty);
		set => SetValue(StartLocatorProperty, value);
	}

	public ImageSource CoverImageSource
	{
		get => (ImageSource)GetValue(CoverImageSourceProperty);
		set => SetValue(CoverImageSourceProperty, value);
	}

	public event EventHandler<ReaderMessageEventArgs>? MessageReceived;
	public event EventHandler? ReaderReady;
	public event EventHandler<EpubLocator>? LocationChanged;
	public event EventHandler? ExitRequested;
	public event EventHandler? SettingsRequested;
	public event EventHandler<string>? ThemeChanged;
	public event EventHandler<bool>? ChromeVisibilityChanged;
	public event EventHandler<string>? ReaderError;

	public async Task LoadPublicationAsync(CancellationToken cancellationToken = default)
	{
		EpubArchive? publicationSource = PublicationSource;
		string opfPath = PublicationOpfPath;
		if (publicationSource is null || string.IsNullOrWhiteSpace(opfPath))
		{
			return;
		}

		if (hasLoadedPublication || isLoading)
		{
			return;
		}

		cancellationToken.ThrowIfCancellationRequested();
		isLoading = true;
		LoadingOverlay.IsVisible = true;
		try
		{
			// Parsing the OPF/spine/TOC and assembling every chapter into one combined document
			// usually isn't work this method does at all any more: whoever supplied the archive
			// is expected to have started the same memoized call while the user was still on the
			// details page (see the App project's EpubArchivePrefetchCache), leaving this await
			// to complete immediately. It stays here so a publication that arrives without that
			// head start -- or one whose prefetch was still running -- still loads correctly,
			// just without the saving. The token is applied to the wait rather than to the work,
			// since that work is shared and must not be cancelled on another caller's behalf.
			EpubPublicationInfo publication = await EpubPublicationLoader
				.PrepareAsync(publicationSource)
				.WaitAsync(cancellationToken);

			// Wires the resource host (WebResourceRequested/asset loader/URL scheme handler,
			// depending on platform) to this book's archive -- cheap and safe to redo on every
			// book, whether or not the shell itself needs a fresh navigation below.
			await assetHost.InitializeAsync(ReaderWebView, publicationSource, HandleNativeNavigationAsync, HandleDictionaryLookupRequested, cancellationToken);
			hasLoadedPublication = true;
			isReadyForLocationChanges = false;
			readerReadyReceived = false;
			pendingStartLocator = null;

			await EnsureReaderShellLoadedAsync(cancellationToken);
			await SendLoadPublicationAsync(publication, cancellationToken);
		}
		finally
		{
			isLoading = false;
		}
	}

	/// <summary>
	/// Navigates to the reader shell (index.html/EpubText.js) the first time this control's
	/// WebView is used, and simply returns once that's already happened for every later book --
	/// see the isShellReady/shellReadyTcs remarks above. Waits for the shell's own "shellReady"
	/// bridge message (fired once EpubText.js has finished its book-independent boot: event
	/// bindings, reader stylesheet preload) rather than the WebView's Navigated event, since the
	/// latter only means the HTML document loaded, not that the script running inside it is ready
	/// to receive a book.
	/// </summary>
	async Task EnsureReaderShellLoadedAsync(CancellationToken cancellationToken)
	{
		if (isShellReady)
		{
			return;
		}

		if (shellReadyTcs is null)
		{
			shellReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			ReaderWebView.Source = new UrlWebViewSource
			{
				Url = assetHost.GetShellUri().ToString()
			};
		}

		await shellReadyTcs.Task.WaitAsync(cancellationToken);
	}

	async Task SendLoadPublicationAsync(EpubPublicationInfo publication, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// The shell (index.html) is served from "DisplayBookViewer/"; resolving a book-relative
		// path against that base without escaping back out of it first would 404 every request --
		// see GetBookRelativePath.
		string combinedHref = EpubPathUtilities.GetBookRelativePath(CombinedDocumentBuilder.CombinedDocumentPath);
		string? coverHref = publication.CoverHref is null ? null : EpubPathUtilities.GetBookRelativePath(publication.CoverHref);
		ReaderPublicationPayload payload = new(combinedHref, publication.Title, publication.Author, publication.Spine, publication.Toc, coverHref);
		string json = JsonSerializer.Serialize(payload, ReaderJsonContext.Default.ReaderPublicationPayload);
		string script = $"window.DisplayBookReader?.loadPublication({json});";
		await ReaderWebView.EvaluateJavaScriptAsync(script);
	}

	public async Task SetLocatorAsync(EpubLocator locator, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		System.Diagnostics.Debug.WriteLine(
			$"[EpubReaderView] SetLocatorAsync -> ResourceHref={locator.ResourceHref}, Page={locator.Page}, CharOffset={locator.CharOffset}");
		string resource = JsonSerializer.Serialize(locator.ResourceHref, ReaderJsonContext.Default.String);
		string script = $"window.DisplayBookReader?.setLocator({resource}, {locator.Page}, {locator.CharOffset});";
		await ReaderWebView.EvaluateJavaScriptAsync(script);
	}

[SuppressMessage("Security", "S1172", Justification = "Unused method parameters should be removed.")]
	static void OnCoverImageSourceChanged(BindableObject bindable, object oldValue, object newValue)
	{
		EpubReaderView reader = (EpubReaderView)bindable;
		reader.LoadingCoverImage.Source = (ImageSource?)newValue;
	}

[SuppressMessage("Security", "S1172", Justification = "Unused method parameters should be removed.")]
	static void OnPublicationChanged(BindableObject bindable, object oldValue, object newValue)
	{
		EpubReaderView reader = (EpubReaderView)bindable;

		// Reset unconditionally, not just when IsLoaded: the ViewModel can set
		// PublicationSource/PublicationOpfPath before the page is pushed onto the visual
		// tree, so IsLoaded is still false here. Leaving hasLoadedPublication set would
		// make the next OnLoaded/TryLoadPublicationAsync call silently skip loading.
		reader.hasLoadedPublication = false;
		if (!reader.IsLoaded)
		{
			return;
		}

		// PublicationSource and PublicationOpfPath (bound to Book.PublicationOpfPath) are set as
		// two separate steps by ReaderViewModel.InitializeAsync, each firing this callback
		// synchronously in turn -- so the first firing here can see the NEW PublicationSource
		// paired with the OLD PublicationOpfPath (or vice versa) for one instant. That was
		// harmless while every reload was a full page navigation (the second, correct navigation
		// simply superseded the first, stale one before it finished) but matters now that a
		// same-shell reload calls straight into already-running JS -- an in-between, inconsistent
		// pairing would really be sent to it. Dispatching the actual reload, and coalescing a
		// second firing that arrives before the dispatched one runs, defers the real work until
		// after both properties have settled, so it only ever sees the final, consistent pairing.
		if (reader.reloadDispatchQueued)
		{
			return;
		}

		reader.reloadDispatchQueued = true;
		reader.Dispatcher.Dispatch(async () =>
		{
			reader.reloadDispatchQueued = false;
			try
			{
				await reader.ReloadPublicationAsync();
			}
			catch (Exception exception)
			{
				reader.RaiseReaderError(exception.Message);
			}
		});
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		await TryLoadPublicationAsync();
	}

	async void OnReaderWebViewHandlerChanged(object? sender, EventArgs e)
	{
		// A handler change can mean the underlying native WebView was actually torn down and
		// replaced (e.g. some platform disposing/recreating it across a page pop/push) rather
		// than the same instance simply being reattached. There's no cheap, cross-platform way to
		// tell those two cases apart here, and getting it wrong the optimistic way (assuming the
		// shell survived when it didn't) would leave the reader stuck sending loadPublication to a
		// WebView that was never navigated to the shell at all. Resetting unconditionally instead
		// just costs one extra shell navigation in the case where the native view did survive.
		hasLoadedPublication = false;
		isShellReady = false;
		shellReadyTcs = null;
		if (IsLoaded)
		{
			await TryLoadPublicationAsync();
		}
	}

	async Task TryLoadPublicationAsync()
	{
		if (hasLoadedPublication)
		{
			return;
		}

		try
		{
			await LoadPublicationAsync();
		}
		catch (Exception exception)
		{
			RaiseReaderError(exception.Message);
		}
	}

	async Task ReloadPublicationAsync()
	{
		try
		{
			await LoadPublicationAsync();
		}
		catch (Exception exception)
		{
			RaiseReaderError(exception.Message);
		}
	}

	async void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
	{
		try
		{
			await HandleNavigationAsync(e.Url, cancelNavigation: () => e.Cancel = true);
		}
		catch (Exception exception)
		{
			RaiseReaderError(exception.Message);
		}
	}

	Task HandleNativeNavigationAsync(string url)
	{
		return HandleNavigationAsync(url, cancelNavigation: null);
	}

	void HandleDictionaryLookupRequested(string selection)
	{
		_ = HandleDictionaryLookupRequestedAsync(selection);
	}

	async Task HandleDictionaryLookupRequestedAsync(string selection)
	{
		try
		{
			DictionaryDefinition? result = await dictionaryLookupService.LookupAsync(selection);
			ShowDefinition(selection, result);
		}
		catch (Exception exception)
		{
			RaiseReaderError(exception.Message);
		}
	}

	async Task HandleNavigationAsync(string? url, Action? cancelNavigation)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
			!string.Equals(uri.Scheme, "displaybook", StringComparison.OrdinalIgnoreCase) ||
			!string.Equals(uri.Host, "bridge", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		cancelNavigation?.Invoke();
		string? rawMessage = GetQueryParameter(uri, "message");
		if (string.IsNullOrWhiteSpace(rawMessage))
		{
			RaiseReaderError("The reader bridge sent an empty message.");
			return;
		}

		try
		{
			ReaderBridgeMessage? message = JsonSerializer.Deserialize(rawMessage, ReaderJsonContext.Default.ReaderBridgeMessage);
			if (message is not null)
			{
				await HandleMessageAsync(message);
			}
		}
		catch (JsonException exception)
		{
			RaiseReaderError($"The reader bridge sent invalid JSON: {exception.Message}");
		}
	}

	void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
	{
		if (e.Result != WebNavigationResult.Success)
		{
			RaiseReaderError($"The reader WebView could not load the publication ({e.Result}).");
		}
	}

	async Task HandleMessageAsync(ReaderBridgeMessage message)
	{
		MessageReceived?.Invoke(this, new ReaderMessageEventArgs(message));
		switch (message.Type)
		{
			case ReaderBridgeMessageTypes.ShellReady:
				isShellReady = true;
				shellReadyTcs?.TrySetResult(true);
				break;
			case ReaderBridgeMessageTypes.ReaderReady:
				if (readerReadyReceived)
				{
					break;
				}

				readerReadyReceived = true;
				System.Diagnostics.Debug.WriteLine(
					$"[EpubReaderView] readerReady received; StartLocator=({StartLocator.ResourceHref}, Page={StartLocator.Page}, CharOffset={StartLocator.CharOffset})");
				if (string.IsNullOrWhiteSpace(StartLocator.ResourceHref))
				{
					CompleteInitialReaderReady();
				}
				else
				{
					pendingStartLocator = StartLocator;
					await SetLocatorAsync(StartLocator);
				}
				break;
			case ReaderBridgeMessageTypes.LocationChanged:
				EpubLocator? locator = message.Payload.Deserialize(ReaderJsonContext.Default.EpubLocator);
				if (locator is not null)
				{
					System.Diagnostics.Debug.WriteLine(
						$"[EpubReaderView] locationChanged -> ResourceHref={locator.ResourceHref}, Page={locator.Page}, PageCount={locator.PageCount}, CharOffset={locator.CharOffset}, isReadyForLocationChanges={isReadyForLocationChanges}, hasPendingStartLocator={pendingStartLocator is not null}");
					if (!isReadyForLocationChanges)
					{
						// Not matched by ResourceHref: the JS setLocator() call this responds
						// to always resolves to *some* chapter and always reports exactly one
						// locationChanged when it's done (falling back to the current chapter
						// if the requested one can't be found, e.g. an old locator format or a
						// locator synced from a device whose copy of the book doesn't line up)
						// — and it's also free to land on a different page than requested, e.g.
						// when resolving a CharOffset to wherever that text falls on this
						// device's pagination. Since this is the only source of a
						// locationChanged before the reader is marked ready, the first one to
						// arrive here is unambiguously the response to that call.
						if (pendingStartLocator is null)
						{
							break;
						}

						pendingStartLocator = null;
						CompleteInitialReaderReady();
					}

					LocationChanged?.Invoke(this, locator);
				}
				break;
			case ReaderBridgeMessageTypes.RequestExit:
				ExitRequested?.Invoke(this, EventArgs.Empty);
				break;
			case ReaderBridgeMessageTypes.RequestSettings:
				SettingsRequested?.Invoke(this, EventArgs.Empty);
				break;
			case ReaderBridgeMessageTypes.ThemeChanged:
				if (message.Payload.TryGetProperty("theme", out JsonElement themeElement) &&
					themeElement.ValueKind == JsonValueKind.String &&
					!string.IsNullOrWhiteSpace(themeElement.GetString()))
				{
					ThemeChanged?.Invoke(this, themeElement.GetString()!);
				}
				break;
			case ReaderBridgeMessageTypes.ChromeVisibilityChanged:
				if (message.Payload.TryGetProperty("visible", out JsonElement visibleElement) &&
					visibleElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
				{
					ChromeVisibilityChanged?.Invoke(this, visibleElement.GetBoolean());
				}
				break;
			case ReaderBridgeMessageTypes.ReaderError:
				string? error = message.Payload.TryGetProperty("message", out JsonElement messageElement)
					? messageElement.GetString()
					: message.Payload.ToString();
				RaiseReaderError(string.IsNullOrWhiteSpace(error) ? "The reader could not open the publication." : error);
				break;
			case ReaderBridgeMessageTypes.DictionaryLookupRequested:
				if (message.Payload.TryGetProperty("text", out JsonElement selectionElement) &&
					selectionElement.ValueKind == JsonValueKind.String &&
					!string.IsNullOrWhiteSpace(selectionElement.GetString()))
				{
					HandleDictionaryLookupRequested(selectionElement.GetString()!);
				}
				break;
		}
	}

	void RaiseReaderError(string message)
	{
		LoadingOverlay.IsVisible = false;
		ReaderError?.Invoke(this, message);
	}

	void ShowDefinition(string selection, DictionaryDefinition? result)
	{
		DefinitionWordLabel.Text = result?.Word ?? selection.Trim();
		DefinitionTextLabel.Text = result?.Definition ?? $"No definition found for “{selection.Trim()}”.";
		DefinitionOverlay.IsVisible = true;
	}

	async void OnDefinitionScrimTapped(object? sender, TappedEventArgs e)
	{
		DefinitionOverlay.IsVisible = false;
		await ClearSelectionAsync();
	}

	void OnDefinitionCardTapped(object? sender, TappedEventArgs e)
	{
		// Intentionally empty: swallows the tap so it doesn't bubble to the scrim's
		// dismiss handler when the user taps inside the definition card.
	}

	async void OnDefinitionCloseClicked(object? sender, EventArgs e)
	{
		DefinitionOverlay.IsVisible = false;
		await ClearSelectionAsync();
	}

	async Task ClearSelectionAsync()
	{
		try
		{
			await ReaderWebView.EvaluateJavaScriptAsync("window.DisplayBookReader?.clearSelection();");
		}
		catch (Exception exception)
		{
			RaiseReaderError(exception.Message);
		}
	}

	void CompleteInitialReaderReady()
	{
		isReadyForLocationChanges = true;
		LoadingOverlay.IsVisible = false;
		ReaderReady?.Invoke(this, EventArgs.Empty);
	}

	static string? GetQueryParameter(Uri uri, string name)
	{
		foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = pair.Split('=', 2);
			if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.Ordinal))
			{
				return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
			}
		}

		return null;
	}
}