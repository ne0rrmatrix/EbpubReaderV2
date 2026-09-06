using DisplayBook.App.ViewModels;
using DisplayBook.Viewer.Controls;
using DisplayBook.Viewer.Models;

namespace DisplayBook.App.Views;

public partial class ReaderPage : ContentPage
{
    private readonly ReaderViewModel _viewModel;
#if ANDROID
    private Android.Graphics.Color? _previousStatusBarColor;
    private Android.Views.SystemUiFlags _previousSystemUiFlags;
    private bool _readerSystemUiConfigured;
    private string _readerTheme = "sepia";
#endif

    public ReaderPage(ReaderViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        ConfigureReaderSystemUi();
#endif
        Reader.LocationChanged += OnLocationChanged;
        Reader.ExitRequested += OnExitRequested;
#if ANDROID
        Reader.ThemeChanged += OnReaderThemeChanged;
#endif
    }

    protected override void OnDisappearing()
    {
        Reader.LocationChanged -= OnLocationChanged;
        Reader.ExitRequested -= OnExitRequested;
#if ANDROID
        Reader.ThemeChanged -= OnReaderThemeChanged;
#endif
#if ANDROID
        RestoreSystemUi();
#endif
        base.OnDisappearing();
    }

#if ANDROID
    private void ConfigureReaderSystemUi()
    {
        var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
        if (window is null || _readerSystemUiConfigured)
        {
            return;
        }

        _previousStatusBarColor = new Android.Graphics.Color(window.StatusBarColor);
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            _previousSystemUiFlags = window.DecorView.SystemUiFlags;
        }
        SetStatusBarColor(window);

        UpdateStatusBarAppearance(window);
        _readerSystemUiConfigured = true;
    }

    private void OnReaderThemeChanged(object? sender, string theme)
    {
        _readerTheme = theme;
        var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
        if (window is not null && _readerSystemUiConfigured)
        {
            SetStatusBarColor(window);
            UpdateStatusBarAppearance(window);
        }
    }

    private void SetStatusBarColor(Android.Views.Window window)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            return;
        }

        window.SetStatusBarColor(_readerTheme.Equals("night", StringComparison.OrdinalIgnoreCase)
            ? Android.Graphics.Color.Rgb(28, 36, 48)
            : Android.Graphics.Color.Rgb(246, 241, 232));
    }

    private void UpdateStatusBarAppearance(Android.Views.Window window)
    {
        var useDarkStatusBarIcons = !_readerTheme.Equals("night", StringComparison.OrdinalIgnoreCase);

        if (OperatingSystem.IsAndroidVersionAtLeast(30) && window.InsetsController is { } insetsController)
        {
            const int lightStatusBarsAppearance = 8;
            var appearance = useDarkStatusBarIcons ? lightStatusBarsAppearance : 0;
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

    private void RestoreSystemUi()
    {
        var window = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window;
        if (window is null || !_readerSystemUiConfigured)
        {
            return;
        }

        if (_previousStatusBarColor is { } previousStatusBarColor && !OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            window.SetStatusBarColor(previousStatusBarColor);
        }
        if(!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            window.DecorView.SystemUiFlags = _previousSystemUiFlags;
        }
       
        _readerSystemUiConfigured = false;
    }
#endif

    private async void OnLocationChanged(object? sender, EpubLocator locator)
    {
        if (sender is null)
        {
            return;
        }

        await _viewModel.UpdateLocatorAsync(locator);
    }

    private async void OnExitRequested(object? sender, EventArgs e)
    {
        if (sender is null)
        {
            return;
        }

        await _viewModel.ExitReaderCommand.ExecuteAsync(null);
    }
}
