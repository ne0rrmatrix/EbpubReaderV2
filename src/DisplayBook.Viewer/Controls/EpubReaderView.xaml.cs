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
	EpubLocator? pendingStartLocator;

	public static readonly BindableProperty PublicationRootProperty = BindableProperty.Create(
		nameof(PublicationRoot),
		typeof(string),
		typeof(EpubReaderView),
		string.Empty,
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

	public string PublicationRoot
	{
		get => (string)GetValue(PublicationRootProperty);
		set => SetValue(PublicationRootProperty, value);
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
	public event EventHandler<string>? ReaderError;

	public async Task LoadPublicationAsync(CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(PublicationRoot) || string.IsNullOrWhiteSpace(PublicationOpfPath))
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
			await assetHost.InitializeAsync(ReaderWebView, HandleNativeNavigationAsync, HandleDictionaryLookupRequested, cancellationToken);
			hasLoadedPublication = true;
			isReadyForLocationChanges = false;
			readerReadyReceived = false;
			pendingStartLocator = null;
			ReaderWebView.Source = new UrlWebViewSource
			{
				Url = assetHost.GetViewerUri(PublicationRoot, PublicationOpfPath).ToString()
			};
		}
		finally
		{
			isLoading = false;
		}
	}

	public async Task SetLocatorAsync(EpubLocator locator, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string resource = JsonSerializer.Serialize(locator.ResourceHref, ReaderJsonContext.Default.String);
		string script = $"window.DisplayBookReader?.setLocator({resource}, {locator.Page}, {locator.CharOffset});";
		await ReaderWebView.EvaluateJavaScriptAsync(script);
	}

	static void OnCoverImageSourceChanged(BindableObject bindable, object oldValue, object newValue)
	{
		EpubReaderView reader = (EpubReaderView)bindable;
		reader.LoadingCoverImage.Source = (ImageSource?)newValue;
	}

	static async void OnPublicationChanged(BindableObject bindable, object oldValue, object newValue)
	{
		EpubReaderView reader = (EpubReaderView)bindable;
		try
		{
			if (reader.IsLoaded)
			{
				reader.hasLoadedPublication = false;
				await reader.ReloadPublicationAsync();
			}
		}
		catch (Exception exception)
		{
			reader.RaiseReaderError(exception.Message);
		}
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		await TryLoadPublicationAsync();
	}

	async void OnReaderWebViewHandlerChanged(object? sender, EventArgs e)
	{
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
			case ReaderBridgeMessageTypes.ReaderReady:
				if (readerReadyReceived)
				{
					break;
				}

				readerReadyReceived = true;
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