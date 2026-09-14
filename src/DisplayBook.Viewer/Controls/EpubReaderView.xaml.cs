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
		await TryLoadPublicationAsync();
	}

	async void OnReaderWebViewHandlerChanged(object? sender, EventArgs e)
	{
		loadedPublicationSource = null;
		loadedPublicationOpfPath = null;
		isShellReady = false;
		shellReadyTcs = null;
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