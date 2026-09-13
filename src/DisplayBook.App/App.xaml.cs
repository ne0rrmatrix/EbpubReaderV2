using DisplayBook.Viewer.Services;

namespace DisplayBook.App;

public partial class App : Application
{
	readonly AppShell appShell;

	public App(AppShell appShell)
	{
		this.appShell = appShell;
		InitializeComponent();

		// The reader shell's own assets (index.html/EpubText.js/CSS) are the same for every book
		// and don't require one to be open, so they're read out of the app package here rather
		// than during the first book open, where they were pure added latency on the path between
		// the user tapping Open and seeing a page.
		PreloadReaderShellAssets();
	}

	/// <summary>
	/// Starts the shell-asset preload without awaiting it -- startup must not block on it, and
	/// the reader awaits the same work itself if it somehow gets there first. A failure here is
	/// not fatal (<see cref="ReaderAssetHost"/> simply retries the read when a book is opened),
	/// but it is still observed rather than discarded, so it surfaces in the log instead of as an
	/// UnobservedTaskException at some unrelated later GC.
	/// </summary>
	static void PreloadReaderShellAssets()
	{
		ReaderAssetHost.PreloadShellAssetsAsync().ContinueWith(
			static faulted => System.Diagnostics.Trace.TraceError(
				$"Preloading the reader shell assets failed; they will be re-read when a book is opened. {faulted.Exception}"),
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return appShell is null ? new Window(new AppShell()) : new Window(appShell);
	}
}