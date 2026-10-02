// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DisplayBook.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		this.InitializeComponent();

		// Spinning up the WebView2 runtime's browser process is the slowest part of getting the
		// reader ready, and it's a per-process resource shared by every CoreWebView2 the app ever
		// creates. Starting it here -- the earliest point in the app's lifetime -- lets it
		// overlap with the rest of startup (library load, etc.) instead of blocking the first
		// time the reader page actually needs a WebView2. Any failure surfaces later, when
		// ReaderAssetHost awaits this same cached task for real.
		_ = DisplayBook.Viewer.Services.ReaderAssetHost.WarmUpEnvironmentAsync();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}