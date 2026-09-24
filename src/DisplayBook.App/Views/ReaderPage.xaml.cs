using DisplayBook.App.ViewModels;
using DisplayBook.Viewer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific;
#if ANDROID
using AndroidX.Core.View;
#endif

namespace DisplayBook.App.Views;

public partial class ReaderPage : ContentPage, IQueryAttributable
{
	readonly ReaderViewModel viewModel;
	readonly ILogger<ReaderPage> logger;
	Window? lifecycleWindow;
#if ANDROID
	WindowInsetsControllerCompat? readerInsetsController;
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
		SubscribeToWindowLifecycle();
	}

	protected override void OnDisappearing()
	{
		Reader.LocationChanged -= OnLocationChanged;
		Reader.ExitRequested -= OnExitRequested;
		Reader.ChromeVisibilityChanged -= OnReaderChromeVisibilityChanged;
		UnsubscribeFromWindowLifecycle();
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
#if ANDROID
		SetReaderSystemBarsVisible(isChromeVisible);
#endif
	}

	void SubscribeToWindowLifecycle()
	{
		UnsubscribeFromWindowLifecycle();
		if (Window is not { } window)
		{
			return;
		}

		lifecycleWindow = window;
		window.Activated += OnWindowActivated;
		window.Deactivated += OnWindowDeactivated;
	}

	void UnsubscribeFromWindowLifecycle()
	{
		if (lifecycleWindow is null)
		{
			return;
		}

		lifecycleWindow.Activated -= OnWindowActivated;
		lifecycleWindow.Deactivated -= OnWindowDeactivated;
		lifecycleWindow = null;
	}

	/// <summary>
	/// The app is losing focus -- quite possibly because the user is about to pick the book up
	/// on another device -- so send the debounced position now rather than up to a few seconds
	/// later, when the OS may already have suspended the app.
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S1172:Unused method parameters should be removed", Justification = "It is an event handler")]
	async void OnWindowDeactivated(object? sender, EventArgs e) => await FlushPendingSyncAsync();

	/// <summary>
	/// The app is back in the foreground: if the book was read further on another device in
	/// the meantime, offer to jump there (see ReaderViewModel.CheckForNewerRemotePositionAsync).
	/// </summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S1172:Unused method parameters should be removed", Justification = "It is an event handler")]
	async void OnWindowActivated(object? sender, EventArgs e)
	{
		try
		{
			if (await viewModel.CheckForNewerRemotePositionAsync() is { } locator)
			{
				await Reader.SetLocatorAsync(locator);
			}
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not check for a newer synced reading position.");
		}
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
		if (window?.DecorView is not { } decorView || readerSystemUiConfigured)
		{
			return;
		}

		if (WindowCompat.GetInsetsController(window, decorView) is not { } controller)
		{
			return;
		}

		readerInsetsController = controller;
		controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;

		SetStatusBarColor(window);
		UpdateStatusBarAppearance();

		// The reader's own JS/CSS starts in immersive (chrome-hidden) mode, so the native
		// system bars should start hidden too, matching the iOS SetPrefersStatusBarHidden
		// call in OnAppearing rather than waiting for the first chromeVisibilityChanged
		// bridge message.
		readerInsetsController.Hide(WindowInsetsCompat.Type.SystemBars());
		readerSystemUiConfigured = true;
	}

	void OnReaderThemeChanged(object? sender, string theme)
	{
		readerTheme = theme;
		Android.Views.Window? window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
		if (window is not null && readerSystemUiConfigured)
		{
			SetStatusBarColor(window);
			UpdateStatusBarAppearance();
		}
	}

	void SetStatusBarColor(Android.Views.Window window)
	{
		if (OperatingSystem.IsAndroidVersionAtLeast(35))
		{
			return;
		}

		Android.Graphics.Color color = readerTheme.Equals("night", StringComparison.OrdinalIgnoreCase)
			? Android.Graphics.Color.Rgb(28, 36, 48)
			: Android.Graphics.Color.Rgb(246, 241, 232);
		window.SetStatusBarColor(color);
		window.SetNavigationBarColor(color);
	}

	void UpdateStatusBarAppearance()
	{
		if (readerInsetsController is not { } controller)
		{
			return;
		}

		bool useDarkIcons = !readerTheme.Equals("night", StringComparison.OrdinalIgnoreCase);
		controller.AppearanceLightStatusBars = useDarkIcons;
		controller.AppearanceLightNavigationBars = useDarkIcons;
	}

	void SetReaderSystemBarsVisible(bool isVisible)
	{
		if (readerInsetsController is not { } controller)
		{
			return;
		}

		int systemBars = WindowInsetsCompat.Type.SystemBars();
		if (isVisible)
		{
			controller.Show(systemBars);
		}
		else
		{
			controller.Hide(systemBars);
		}
	}

	void RestoreSystemUi()
	{
		if (!readerSystemUiConfigured)
		{
			return;
		}

		readerInsetsController?.Show(WindowInsetsCompat.Type.SystemBars());
		readerInsetsController = null;
		readerSystemUiConfigured = false;

		// Recompute the app's normal status/nav bar color, icon appearance, and cutout
		// handling from the live app theme (MainActivity.ConfigureSystemBars) rather than
		// replaying a snapshot captured at reader-entry time - a stale snapshot is what
		// previously left the bar showing the reader's last color/icon scheme (e.g. light
		// sepia icons) over a since-changed, possibly dark-mode, app theme.
		(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity)?.ConfigureSystemBars();
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