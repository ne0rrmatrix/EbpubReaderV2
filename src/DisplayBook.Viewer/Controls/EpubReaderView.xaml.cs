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
	bool isLoading;
	bool isReadyForLocationChanges;
	bool loadRequestedWhileLoading;
	bool readerReadyReceived;
	bool reloadDispatchQueued;
	EpubLocator? pendingStartLocator;

	EpubArchive? loadedPublicationSource;
	string? loadedPublicationOpfPath;

	bool isShellReady;
	TaskCompletionSource<bool>? shellReadyTcs;
	Window? lifecycleWindow;

	/// <summary>
	/// How long to wait for the reader shell's <c>shellReady</c> bridge message before giving up on
	/// that navigation and starting it over. Generous, because the wait covers a real WebView
	/// navigation on a cold, possibly slow device -- but bounded, because an unbounded wait here is
	/// unrecoverable: it leaves <c>isLoading</c> stuck on, which turns every later load request into
	/// a no-op (see <see cref="LoadPublicationAsync"/>) and strands the loading overlay on screen
	/// until the app is force-quit.
	/// </summary>
	static readonly TimeSpan shellReadyTimeout = TimeSpan.FromSeconds(20);

	/// <summary>
	/// Bound separately from <see cref="shellReadyTimeout"/>: this one only covers a single
	/// already-loaded round trip into JS, so it can be short. It exists so a WebView whose web
	/// content process is gone -- which is exactly what the probe is trying to detect -- can't hang
	/// the probe instead of answering it.
	/// </summary>
	static readonly TimeSpan readerLivenessProbeTimeout = TimeSpan.FromSeconds(5);

	/// <summary>
	/// How many times <see cref="EnsureReaderShellLoadedAsync"/> will start a fresh shell
	/// navigation before giving up and surfacing an error. One retry is enough to clear the case
	/// this exists for -- a navigation stranded by a torn-down web content process -- while still
	/// failing fast enough that the user isn't left watching the overlay for a full minute.
	/// </summary>
	const int shellReadyAttempts = 2;

	/// <summary>
	/// Several methods here touch only x:Name fields declared in EpubReaderView.xaml. Those fields
	/// are produced by the XAML source generator, which SonarLint doesn't see, so it reports them
	/// as using no instance state. Making any of them static would not compile.
	/// </summary>
	const string generatedXamlFieldJustification =
		"Accesses x:Name fields generated from EpubReaderView.xaml; the method cannot be static.";

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
		Unloaded += OnUnloaded;
		ReaderWebView.HandlerChanged += OnReaderWebViewHandlerChanged;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = generatedXamlFieldJustification)]
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
		if (isLoading)
		{
			// Deliberately not a plain return: see the remarks above. The in-flight load re-checks
			// this flag when it finishes and picks up whatever is bound by then.
			loadRequestedWhileLoading = true;
			return;
		}

		isLoading = true;
		try
		{
			do
			{
				// Cleared before the load, not after, so a request arriving while it runs is
				// still seen by the loop condition below.
				loadRequestedWhileLoading = false;
				await LoadCurrentPublicationAsync(cancellationToken);
			}
			while (loadRequestedWhileLoading);
		}
		finally
		{
			isLoading = false;
		}
	}

	/// <summary>
	/// One pass of <see cref="LoadPublicationAsync"/>: loads the currently bound publication, or
	/// returns immediately if there isn't one or it's already the one on screen. That second check
	/// compares against what the last load actually delivered (see loadedPublicationSource) rather
	/// than a flag, which is what lets a superseding pass tell "the book changed while I was
	/// loading" apart from "nothing to do" even though the pass it's superseding ran to completion.
	/// </summary>
	async Task LoadCurrentPublicationAsync(CancellationToken cancellationToken)
	{
		EpubArchive? publicationSource = PublicationSource;
		string opfPath = PublicationOpfPath;
		if (publicationSource is null || string.IsNullOrWhiteSpace(opfPath))
		{
			return;
		}

		if (ReferenceEquals(publicationSource, loadedPublicationSource) &&
			string.Equals(opfPath, loadedPublicationOpfPath, StringComparison.Ordinal))
		{
			return;
		}

		cancellationToken.ThrowIfCancellationRequested();
		// Also attempted from OnLoaded, but Window can still be null that early on some platforms.
		// Re-attempting here (idempotent) guarantees the subscription exists for as long as there's
		// an open book to recover, which is the only time it matters.
		SubscribeToWindowLifecycle();
		LoadingOverlay.IsVisible = true;

		EpubPublicationInfo publication = await EpubPublicationLoader
			.PrepareAsync(publicationSource)
			.WaitAsync(cancellationToken);

		await assetHost.InitializeAsync(ReaderWebView, publicationSource, HandleNativeNavigationAsync, HandleDictionaryLookupRequested, cancellationToken);
		isReadyForLocationChanges = false;
		readerReadyReceived = false;
		pendingStartLocator = null;

		await EnsureReaderShellLoadedAsync(cancellationToken);
		await SendLoadPublicationAsync(publication, cancellationToken);
		
		loadedPublicationSource = publicationSource;
		loadedPublicationOpfPath = opfPath;
	}

	/// <summary>
	/// Waits for the reader shell to report <c>shellReady</c>, starting the shell navigation first
	/// if nothing else has. The shell is navigated to at most once per app session, so on all but
	/// the first book this returns immediately.
	/// </summary>
	/// <remarks>
	/// The wait is bounded and retried once rather than open-ended. A shell navigation that never
	/// reports ready is a real, recurring state -- it's what the WebView is left in when the OS
	/// tears down its web content process (or, on Android, recreates the activity) while the app
	/// sits in the background, and the shell navigation started before that happened. Waiting on it
	/// forever wedges the whole control: the finally in <see cref="LoadPublicationAsync"/> never
	/// runs, so isLoading stays true and every subsequent load -- including the recovery one --
	/// silently degrades to "queued behind the load that will never finish".
	/// </remarks>
	async Task EnsureReaderShellLoadedAsync(CancellationToken cancellationToken)
	{
		for (int attempt = 1; attempt <= shellReadyAttempts; attempt++)
		{
			if (isShellReady)
			{
				return;
			}

			Task shellReady = (shellReadyTcs ??= BeginShellNavigation()).Task;
			try
			{
				await shellReady.WaitAsync(shellReadyTimeout, cancellationToken);
				return;
			}
			catch (Exception exception) when (
				exception is TimeoutException or OperationCanceledException &&
				!cancellationToken.IsCancellationRequested)
			{
				// Either the wait timed out or the pending navigation was abandoned underneath us
				// (see OnReaderWebViewHandlerChanged). Both mean the same thing: that navigation is
				// never going to report ready, so drop it and start a fresh one.
				System.Diagnostics.Debug.WriteLine(
					$"[EpubReaderView] reader shell did not become ready (attempt {attempt}); restarting shell navigation.");
				ResetShellState();
			}
		}

		throw new TimeoutException("The reader did not finish loading. Try opening the book again.");
	}

	TaskCompletionSource<bool> BeginShellNavigation()
	{
		// Assigned before the navigation starts, never after: shellReady can arrive on the bridge
		// before the Source setter returns, and the handler for it completes whatever is in this
		// field at that moment.
		TaskCompletionSource<bool> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
		shellReadyTcs = pending;
		ReaderWebView.Source = new UrlWebViewSource
		{
			Url = assetHost.GetShellUri().ToString()
		};
		return pending;
	}

	/// <summary>
	/// Forgets everything this control believes about the live WebView, so the next load rebuilds
	/// it from scratch. Any pending shell wait is cancelled rather than dropped -- dropping it is
	/// what leaves a waiter (and with it isLoading) stuck forever.
	/// </summary>
	void ResetShellState()
	{
		isShellReady = false;
		shellReadyTcs?.TrySetCanceled();
		shellReadyTcs = null;
		loadedPublicationSource = null;
		loadedPublicationOpfPath = null;
		isReadyForLocationChanges = false;
		readerReadyReceived = false;
		pendingStartLocator = null;
	}

	[SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = generatedXamlFieldJustification)]
	async Task SendLoadPublicationAsync(EpubPublicationInfo publication, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		string combinedHref = EpubPathUtilities.GetBookRelativePath(CombinedDocumentBuilder.CombinedDocumentPath);
		string? coverHref = publication.CoverHref is null ? null : EpubPathUtilities.GetBookRelativePath(publication.CoverHref);
		ReaderPublicationPayload payload = new(combinedHref, publication.Title, publication.Author, publication.Spine, publication.Toc, coverHref);
		string json = JsonSerializer.Serialize(payload, ReaderJsonContext.Default.ReaderPublicationPayload);
		string script = $"window.DisplayBookReader?.loadPublication({json});";
		await ReaderWebView.EvaluateJavaScriptAsync(script);
	}

	[SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = generatedXamlFieldJustification)]
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

		if (!reader.IsLoaded)
		{
			return;
		}

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
		SubscribeToWindowLifecycle();
		await TryLoadPublicationAsync();
	}

	[SuppressMessage("Security", "S1172", Justification = "Unused method parameters should be removed.")]
	void OnUnloaded(object? sender, EventArgs e)
	{
		if (lifecycleWindow is not null)
		{
			lifecycleWindow.Resumed -= OnWindowResumed;
			lifecycleWindow = null;
		}
	}

	void SubscribeToWindowLifecycle()
	{
		if (Window is not { } window || ReferenceEquals(window, lifecycleWindow))
		{
			return;
		}

		if (lifecycleWindow is not null)
		{
			lifecycleWindow.Resumed -= OnWindowResumed;
		}

		lifecycleWindow = window;
		window.Resumed += OnWindowResumed;
	}

	[SuppressMessage("Security", "S1172", Justification = "Unused method parameters should be removed.")]
	async void OnWindowResumed(object? sender, EventArgs e)
	{
		try
		{
			await RecoverReaderIfNeededAsync();
		}
		catch (Exception exception)
		{
			RaiseReaderError(exception.Message);
		}
	}

	/// <summary>
	/// Re-opens the current book if the WebView came back from the background unable to show it.
	/// </summary>
	/// <remarks>
	/// After the app has been backgrounded for a long stretch, the OS is free to reclaim the
	/// WebView's web content process (WKWebView on iOS/macOS, the renderer on Android) while
	/// leaving the native WebView object -- and therefore every bit of this control's state --
	/// looking perfectly healthy. The JS side is gone with it, so the reader shell, the loaded
	/// publication and window.DisplayBookReader no longer exist, and the calls this control makes
	/// into JS land nowhere: the book never finishes opening and no error is ever raised. Nothing
	/// short of restarting the app recovers, which is exactly what this avoids by noticing on
	/// resume and reloading from scratch. Reload picks up the live StartLocator, which ReaderPage
	/// keeps bound to the reading position as it changes, so recovery resumes where the reader
	/// actually was rather than where the book was first opened.
	/// </remarks>
	async Task RecoverReaderIfNeededAsync()
	{
		if (isLoading || PublicationSource is null)
		{
			// An in-flight load bounds its own wait (see EnsureReaderShellLoadedAsync) and picks up
			// the current binding when it retries, so there's nothing useful to do alongside it.
			return;
		}

		if (loadedPublicationSource is not null && await IsReaderAliveAsync())
		{
			return;
		}

		System.Diagnostics.Debug.WriteLine("[EpubReaderView] reader did not survive backgrounding; reloading the publication.");
		ResetShellState();
		await TryLoadPublicationAsync();
	}

	[SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = generatedXamlFieldJustification)]
	async Task<bool> IsReaderAliveAsync()
	{
		try
		{
			string? result = await ReaderWebView
				.EvaluateJavaScriptAsync("window.DisplayBookReader?.isReaderAlive() ? 'alive' : 'gone'")
				.WaitAsync(readerLivenessProbeTimeout);

			// Platforms disagree about whether a string result comes back quoted, so match on
			// content rather than equality.
			return result?.Contains("alive", StringComparison.Ordinal) == true;
		}
		catch (Exception exception)
		{
			System.Diagnostics.Debug.WriteLine($"[EpubReaderView] reader liveness probe failed: {exception.Message}");
			return false;
		}
	}

	async void OnReaderWebViewHandlerChanged(object? sender, EventArgs e)
	{
		ResetShellState();
		if (IsLoaded)
		{
			await TryLoadPublicationAsync();
		}
	}

	async Task TryLoadPublicationAsync()
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

	/// <summary>
	/// Adapts the async lookup to the void-returning native selection callbacks -- Android's
	/// ActionMode item click and the WinUI context-menu item's CustomItemSelected -- which are the
	/// only two callers. Neither has anywhere to return a Task to, so this is the one place the
	/// lookup can't be awaited; <see cref="HandleDictionaryLookupRequestedAsync"/> therefore handles
	/// its own exceptions rather than letting them escape onto an unobserved task. Callers that
	/// <em>can</em> await (the bridge message path) call that method directly instead.
	/// </summary>
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

	/// <summary>
	/// Dispatches one bridge message. Each case that needs more than a single statement lives in its
	/// own method below, so this stays a flat map from message type to handler.
	/// </summary>
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
				await HandleReaderReadyAsync();
				break;
			case ReaderBridgeMessageTypes.LocationChanged:
				HandleLocationChanged(message);
				break;
			case ReaderBridgeMessageTypes.RequestExit:
				ExitRequested?.Invoke(this, EventArgs.Empty);
				break;
			case ReaderBridgeMessageTypes.RequestSettings:
				SettingsRequested?.Invoke(this, EventArgs.Empty);
				break;
			case ReaderBridgeMessageTypes.ThemeChanged:
				HandleThemeChanged(message);
				break;
			case ReaderBridgeMessageTypes.ChromeVisibilityChanged:
				HandleChromeVisibilityChanged(message);
				break;
			case ReaderBridgeMessageTypes.ReaderError:
				HandleReaderErrorMessage(message);
				break;
			case ReaderBridgeMessageTypes.DictionaryLookupRequested:
				await HandleDictionaryLookupMessageAsync(message);
				break;
		}
	}

	async Task HandleReaderReadyAsync()
	{
		if (readerReadyReceived)
		{
			return;
		}

		readerReadyReceived = true;
		System.Diagnostics.Debug.WriteLine(
			$"[EpubReaderView] readerReady received; StartLocator=({StartLocator.ResourceHref}, Page={StartLocator.Page}, CharOffset={StartLocator.CharOffset})");
		if (string.IsNullOrWhiteSpace(StartLocator.ResourceHref))
		{
			CompleteInitialReaderReady();
			return;
		}

		pendingStartLocator = StartLocator;
		await SetLocatorAsync(StartLocator);
	}

	void HandleLocationChanged(ReaderBridgeMessage message)
	{
		EpubLocator? locator = message.Payload.Deserialize(ReaderJsonContext.Default.EpubLocator);
		if (locator is null)
		{
			return;
		}

		System.Diagnostics.Debug.WriteLine(
			$"[EpubReaderView] locationChanged -> ResourceHref={locator.ResourceHref}, Page={locator.Page}, PageCount={locator.PageCount}, CharOffset={locator.CharOffset}, isReadyForLocationChanges={isReadyForLocationChanges}, hasPendingStartLocator={pendingStartLocator is not null}");
		if (!isReadyForLocationChanges)
		{
			// The handshake completes on the first locationChanged that answers the start locator we
			// asked for; anything arriving before that belongs to the outgoing book and is dropped.
			if (pendingStartLocator is null)
			{
				return;
			}

			pendingStartLocator = null;
			CompleteInitialReaderReady();
		}

		LocationChanged?.Invoke(this, locator);
	}

	void HandleThemeChanged(ReaderBridgeMessage message)
	{
		if (TryGetNonEmptyString(message.Payload, "theme", out string? theme))
		{
			ThemeChanged?.Invoke(this, theme);
		}
	}

	void HandleChromeVisibilityChanged(ReaderBridgeMessage message)
	{
		if (message.Payload.TryGetProperty("visible", out JsonElement visibleElement) &&
			visibleElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			ChromeVisibilityChanged?.Invoke(this, visibleElement.GetBoolean());
		}
	}

	void HandleReaderErrorMessage(ReaderBridgeMessage message)
	{
		string? error = message.Payload.TryGetProperty("message", out JsonElement messageElement)
			? messageElement.GetString()
			: message.Payload.ToString();
		RaiseReaderError(string.IsNullOrWhiteSpace(error) ? "The reader could not open the publication." : error);
	}

	Task HandleDictionaryLookupMessageAsync(ReaderBridgeMessage message) =>
		TryGetNonEmptyString(message.Payload, "text", out string? selection)
			? HandleDictionaryLookupRequestedAsync(selection)
			: Task.CompletedTask;

	static bool TryGetNonEmptyString(JsonElement payload, string propertyName, [NotNullWhen(true)] out string? value)
	{
		value = payload.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String
			? element.GetString()
			: null;
		return !string.IsNullOrWhiteSpace(value);
	}

	void RaiseReaderError(string message)
	{
		LoadingOverlay.IsVisible = false;
		ReaderError?.Invoke(this, message);
	}

	[SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = generatedXamlFieldJustification)]
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