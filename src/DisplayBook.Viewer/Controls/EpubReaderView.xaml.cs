using System.Text.Json;
using DisplayBook.Viewer.Bridge;
using DisplayBook.Viewer.Models;
using DisplayBook.Viewer.Serialization;
using DisplayBook.Viewer.Services;

namespace DisplayBook.Viewer.Controls;

public partial class EpubReaderView : ContentView
{
    private readonly ReaderAssetHost _assetHost = new();
    private readonly IDictionaryLookupService _dictionaryLookupService = new DictionaryLookupService();
    private bool _hasLoadedPublication;
    private bool _isLoading;
    private bool _isReadyForLocationChanges;
    private bool _readerReadyReceived;
    private EpubLocator? _pendingStartLocator;

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

        if (_hasLoadedPublication || _isLoading)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _isLoading = true;
        LoadingOverlay.IsVisible = true;
        try
        {
            await _assetHost.InitializeAsync(ReaderWebView, HandleNativeNavigationAsync, HandleDictionaryLookupRequested, cancellationToken);
            _hasLoadedPublication = true;
            _isReadyForLocationChanges = false;
            _readerReadyReceived = false;
            _pendingStartLocator = null;
            ReaderWebView.Source = new UrlWebViewSource
            {
                Url = _assetHost.GetViewerUri(PublicationRoot, PublicationOpfPath).ToString()
            };
        }
        finally
        {
            _isLoading = false;
        }
    }

    public async Task SetLocatorAsync(EpubLocator locator, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resource = JsonSerializer.Serialize(locator.ResourceHref, ReaderJsonContext.Default.String);
        var script = $"window.DisplayBookReader?.setLocator({resource}, {locator.Page});";
        await ReaderWebView.EvaluateJavaScriptAsync(script);
    }

    private static void OnCoverImageSourceChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var reader = (EpubReaderView)bindable;
        reader.LoadingCoverImage.Source = (ImageSource?)newValue;
    }

    private static async void OnPublicationChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var reader = (EpubReaderView)bindable;
        try
        {
            if (reader.IsLoaded)
            {
                reader._hasLoadedPublication = false;
                await reader.ReloadPublicationAsync();
            }
        }
        catch (Exception exception)
        {
            reader.RaiseReaderError(exception.Message);
        }
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        await TryLoadPublicationAsync();
    }

    private async void OnReaderWebViewHandlerChanged(object? sender, EventArgs e)
    {
        if (IsLoaded)
        {
            await TryLoadPublicationAsync();
        }
    }

    private async Task TryLoadPublicationAsync()
    {
        if (_hasLoadedPublication)
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

    private async Task ReloadPublicationAsync()
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

    private async void OnWebViewNavigating(object? sender, WebNavigatingEventArgs e)
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

    private Task HandleNativeNavigationAsync(string url)
    {
        return HandleNavigationAsync(url, cancelNavigation: null);
    }

    private void HandleDictionaryLookupRequested(string selection)
    {
        _ = HandleDictionaryLookupRequestedAsync(selection);
    }

    private async Task HandleDictionaryLookupRequestedAsync(string selection)
    {
        try
        {
            var result = await _dictionaryLookupService.LookupAsync(selection);
            ShowDefinition(selection, result);
        }
        catch (Exception exception)
        {
            RaiseReaderError(exception.Message);
        }
    }

    private async Task HandleNavigationAsync(string? url, Action? cancelNavigation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "displaybook", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        cancelNavigation?.Invoke();
        var rawMessage = GetQueryParameter(uri, "message");
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            RaiseReaderError("The reader bridge sent an empty message.");
            return;
        }

        try
        {
            var message = JsonSerializer.Deserialize(rawMessage, ReaderJsonContext.Default.ReaderBridgeMessage);
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

    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success)
        {
            RaiseReaderError($"The reader WebView could not load the publication ({e.Result}).");
        }
    }

    private async Task HandleMessageAsync(ReaderBridgeMessage message)
    {
        MessageReceived?.Invoke(this, new ReaderMessageEventArgs(message));
        switch (message.Type)
        {
            case ReaderBridgeMessageTypes.ReaderReady:
                if (_readerReadyReceived)
                {
                    break;
                }

                _readerReadyReceived = true;
                if (string.IsNullOrWhiteSpace(StartLocator.ResourceHref))
                {
                    CompleteInitialReaderReady();
                }
                else
                {
                    _pendingStartLocator = StartLocator;
                    await SetLocatorAsync(StartLocator);
                }
                break;
            case ReaderBridgeMessageTypes.LocationChanged:
                var locator = message.Payload.Deserialize(ReaderJsonContext.Default.EpubLocator);
                if (locator is not null)
                {
                    if (!_isReadyForLocationChanges)
                    {
                        if (_pendingStartLocator is null ||
                            !string.Equals(locator.ResourceHref, _pendingStartLocator.ResourceHref, StringComparison.Ordinal) ||
                            locator.Page != _pendingStartLocator.Page)
                        {
                            break;
                        }

                        _pendingStartLocator = null;
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
                if (message.Payload.TryGetProperty("theme", out var themeElement) &&
                    themeElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(themeElement.GetString()))
                {
                    ThemeChanged?.Invoke(this, themeElement.GetString()!);
                }
                break;
            case ReaderBridgeMessageTypes.ReaderError:
                var error = message.Payload.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : message.Payload.ToString();
                RaiseReaderError(string.IsNullOrWhiteSpace(error) ? "The reader could not open the publication." : error);
                break;
            case ReaderBridgeMessageTypes.DictionaryLookupRequested:
                if (message.Payload.TryGetProperty("text", out var selectionElement) &&
                    selectionElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(selectionElement.GetString()))
                {
                    HandleDictionaryLookupRequested(selectionElement.GetString()!);
                }
                break;
        }
    }

    private void RaiseReaderError(string message)
    {
        LoadingOverlay.IsVisible = false;
        ReaderError?.Invoke(this, message);
    }

    private void ShowDefinition(string selection, DictionaryDefinition? result)
    {
        DefinitionWordLabel.Text = result?.Word ?? selection.Trim();
        DefinitionTextLabel.Text = result?.Definition ?? $"No definition found for “{selection.Trim()}”.";
        DefinitionOverlay.IsVisible = true;
    }

    private void OnDefinitionScrimTapped(object? sender, TappedEventArgs e)
    {
        DefinitionOverlay.IsVisible = false;
        _ = ClearSelectionAsync();
    }

    private void OnDefinitionCardTapped(object? sender, TappedEventArgs e)
    {
        // Intentionally empty: swallows the tap so it doesn't bubble to the scrim's
        // dismiss handler when the user taps inside the definition card.
    }

    private void OnDefinitionCloseClicked(object? sender, EventArgs e)
    {
        DefinitionOverlay.IsVisible = false;
        _ = ClearSelectionAsync();
    }

    private async Task ClearSelectionAsync()
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

    private void CompleteInitialReaderReady()
    {
        _isReadyForLocationChanges = true;
        LoadingOverlay.IsVisible = false;
        ReaderReady?.Invoke(this, EventArgs.Empty);
    }

    private static string? GetQueryParameter(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
            }
        }

        return null;
    }
}
