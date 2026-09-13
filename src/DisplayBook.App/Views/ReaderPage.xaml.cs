using DisplayBook.App.ViewModels;
using DisplayBook.Viewer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;

namespace DisplayBook.App.Views;

public partial class ReaderPage : ContentPage, IQueryAttributable
{
	readonly ReaderViewModel viewModel;
	readonly ILogger<ReaderPage> logger;
#if ANDROID
	Android.Graphics.Color? previousStatusBarColor;
	Android.Views.SystemUiFlags previousSystemUiFlags;
	bool readerSystemUiConfigured;
	string readerTheme = "sepia";
#endif

	public ReaderPage(ReaderViewModel viewModel, ILogger<ReaderPage> logger)
	{
		this.viewModel = viewModel;
		this.logger = logger;
		BindingContext = viewModel;
		InitializeComponent();
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "S3168:Async methods should not return void", Justification = "This method is part of interface contract.")]
	public async void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (query.TryGetValue("id", out object? id) && id is string bookId && !string.IsNullOrWhiteSpace(bookId))
		{
			await viewModel.InitializeAsync(bookId);
		}
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
#if ANDROID
		ConfigureReaderSystemUi();
#endif
		// The reader's own JS/CSS starts in immersive (chrome-hidden) mode, so the native
		// status bar should start hidden to match rather than waiting for the first
		// chromeVisibilityChanged bridge message.
		this.On<iOS>().SetPrefersStatusBarHidden(StatusBarHiddenMode.True);
		Reader.LocationChanged += OnLocationChanged;
		Reader.ExitRequested += OnExitRequested;
		Reader.ChromeVisibilityChanged += OnReaderChromeVisibilityChanged;
#if ANDROID
		Reader.ThemeChanged += OnReaderThemeChanged;
#endif
	}

	protected override void OnDisappearing()
	{
		Reader.LocationChanged -= OnLocationChanged;
		Reader.ExitRequested -= OnExitRequested;
		Reader.ChromeVisibilityChanged -= OnReaderChromeVisibilityChanged;
		this.On<iOS>().SetPrefersStatusBarHidden(StatusBarHiddenMode.Default);
#if ANDROID
		Reader.ThemeChanged -= OnReaderThemeChanged;
#endif
#if ANDROID
		RestoreSystemUi();
#endif
		_ = FlushPendingSyncAsync();
		base.OnDisappearing();
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S1172:Unused method parameters should be removed", Justification = "It is an event handler")]
	void OnReaderChromeVisibilityChanged(object? sender, bool isChromeVisible)
	{
		this.On<iOS>().SetPrefersStatusBarHidden(isChromeVisible ? StatusBarHiddenMode.False : StatusBarHiddenMode.True);
	}

	async Task FlushPendingSyncAsync()
	{
		try
		{
			await viewModel.FlushPendingSyncAsync();
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not flush the pending reading-position sync.");
		}
	}

#if ANDROID
	void ConfigureReaderSystemUi()
	{
		Android.Views.Window? window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
		if (window is null || readerSystemUiConfigured)
		{
			return;
		}

		previousStatusBarColor = new Android.Graphics.Color(window.StatusBarColor);
		if (!OperatingSystem.IsAndroidVersionAtLeast(30))
		{
			previousSystemUiFlags = window.DecorView.SystemUiFlags;
		}
		SetStatusBarColor(window);

		UpdateStatusBarAppearance(window);
		readerSystemUiConfigured = true;
	}

	void OnReaderThemeChanged(object? sender, string theme)
	{
		readerTheme = theme;
		Android.Views.Window? window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
		if (window is not null && readerSystemUiConfigured)
		{
			SetStatusBarColor(window);
			UpdateStatusBarAppearance(window);
		}
	}

	void SetStatusBarColor(Android.Views.Window window)
	{
		if (OperatingSystem.IsAndroidVersionAtLeast(35))
		{
			return;
		}

		window.SetStatusBarColor(readerTheme.Equals("night", StringComparison.OrdinalIgnoreCase)
			? Android.Graphics.Color.Rgb(28, 36, 48)
			: Android.Graphics.Color.Rgb(246, 241, 232));
	}

	void UpdateStatusBarAppearance(Android.Views.Window window)
	{
		bool useDarkStatusBarIcons = !readerTheme.Equals("night", StringComparison.OrdinalIgnoreCase);

		if (OperatingSystem.IsAndroidVersionAtLeast(30) && window.InsetsController is { } insetsController)
		{
			const int lightStatusBarsAppearance = 8;
			int appearance = useDarkStatusBarIcons ? lightStatusBarsAppearance : 0;
			insetsController.SetSystemBarsAppearance(
				appearance,
				lightStatusBarsAppearance);
		}
		if (OperatingSystem.IsAndroidVersionAtLeast(30) || (!OperatingSystem.IsAndroidVersionAtLeast(23)))
		{
			return;
		}

		var systemUiFlags = window.DecorView.SystemUiFlags;
		systemUiFlags |= Android.Views.SystemUiFlags.LayoutStable | Android.Views.SystemUiFlags.LayoutFullscreen;

		if (OperatingSystem.IsAndroidVersionAtLeast(23) && useDarkStatusBarIcons)
		{
			systemUiFlags |= Android.Views.SystemUiFlags.LightStatusBar;
		}
		else
		{
			systemUiFlags &= ~Android.Views.SystemUiFlags.LightStatusBar;
		}

		window.DecorView.SystemUiFlags = systemUiFlags;
	}

	void RestoreSystemUi()
	{
		Android.Views.Window? window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
		if (window is null || !readerSystemUiConfigured)
		{
			return;
		}

		if (previousStatusBarColor is { } previousStatusBarColor1 && !OperatingSystem.IsAndroidVersionAtLeast(35))
		{
			window.SetStatusBarColor(previousStatusBarColor1);
		}
		if (!OperatingSystem.IsAndroidVersionAtLeast(30))
		{
			window.DecorView.SystemUiFlags = previousSystemUiFlags;
		}

		readerSystemUiConfigured = false;
	}
#endif

	async void OnLocationChanged(object? sender, EpubLocator locator)
	{
		if (sender is null)
		{
			return;
		}

		try
		{
			await viewModel.UpdateLocatorAsync(locator);
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not save the reader location.");
		}
	}

	async void OnExitRequested(object? sender, EventArgs e)
	{
		if (sender is null)
		{
			return;
		}

		try
		{
			await viewModel.ExitReaderCommand.ExecuteAsync(null);
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not exit the reader.");
		}
	}
}