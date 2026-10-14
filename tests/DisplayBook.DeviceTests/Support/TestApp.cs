using DisplayBook.App;
using DisplayBook.App.Views;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// Hosts DisplayBook.App's real <see cref="AppShell"/> in a second window of the test app, so
/// pages are created through the app's own DI registrations and Shell routes, and view models'
/// <c>Shell.Current</c> calls reach it (it is the only window whose page is a Shell). The window
/// stays open for the whole run; <see cref="ResetNavigationAsync"/> returns it to the library.
/// Call everything here on the UI thread ([UIFact]).
/// </summary>
static class TestApp
{
	static Window? shellWindow;
	static bool resourcesLoaded;

	public static IServiceProvider Services => IPlatformApplication.Current!.Services;

	public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

	/// <summary>
	/// Adds DisplayBook.App's colours and named styles to the test app's resources, which is where
	/// pages resolve <c>StaticResource</c>. Its implicit (unkeyed) styles are left out: they would
	/// restyle the test runner's own pages, and some of them break its WinUI layout. The colours go
	/// in first because the styles look them up as they load, as they do under the app's App.xaml.
	/// </summary>
	public static void EnsureAppResources()
	{
		if (resourcesLoaded)
		{
			return;
		}

		ResourceDictionary colors = [];
		Copy(new AppColors(), colors);
		Application.Current!.Resources.MergedDictionaries.Add(colors);

		ResourceDictionary styles = [];
		Copy(new AppStyles(), styles);
		Application.Current.Resources.MergedDictionaries.Add(styles);
		resourcesLoaded = true;

		static void Copy(ResourceDictionary source, ResourceDictionary target)
		{
			foreach (ResourceDictionary merged in source.MergedDictionaries)
			{
				Copy(merged, target);
			}

			// Keys and the indexer, not enumeration: enumerating a dictionary skips what it loaded
			// through Source=, which is where all of the app's resources are here.
			foreach (string key in source.Keys)
			{
				object value = source[key];
				bool isImplicitStyle = value is Style style && key == style.TargetType.FullName;
				if (!isImplicitStyle)
				{
					target[key] = value;
				}
			}
		}
	}

	/// <summary>The app shell, opened in its own window on first use and showing the library.</summary>
	public static async Task<Shell> GetShellAsync()
	{
		EnsureAppResources();
		if (shellWindow?.Page is Shell existing)
		{
			return existing;
		}

		AppShell shell = new();
		shellWindow = new Window(shell) { Title = "DisplayBook (device tests)" };
		Application.Current!.OpenWindow(shellWindow);
		await Wait.UntilAsync(() => shell.CurrentPage is LibraryPage { IsLoaded: true }, "the library page to load in the app shell");
		return shell;
	}

	/// <summary>Pops back to the library and closes anything modal.</summary>
	public static async Task ResetNavigationAsync()
	{
		if (shellWindow?.Page is not Shell shell)
		{
			return;
		}

		while (shell.Navigation.ModalStack.Count > 0)
		{
			await shell.Navigation.PopModalAsync(animated: false);
		}

		if (shell.Navigation.NavigationStack.Count > 1)
		{
			await shell.Navigation.PopToRootAsync(animated: false);
		}
	}

	/// <summary>Navigates the shell and waits for a page of the expected type to finish loading.</summary>
	public static async Task<TPage> GoToAsync<TPage>(string route) where TPage : Page
	{
		Shell shell = await GetShellAsync();
		await shell.GoToAsync(route, animate: false);
		return await WaitForPageAsync<TPage>();
	}

	public static async Task<TPage> WaitForPageAsync<TPage>() where TPage : Page
	{
		Shell shell = await GetShellAsync();
		await Wait.UntilAsync(() => shell.CurrentPage is TPage { IsLoaded: true }, $"{typeof(TPage).Name} to be shown");
		return (TPage)shell.CurrentPage;
	}
}

static class Wait
{
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Polls until <paramref name="condition"/> holds. Awaiting on the UI thread keeps the
	/// dispatcher running between checks, so bindings, handlers and WebView messages progress.
	/// Fails the test (TimeoutException) if it never does.
	/// </summary>
	[AssertionMethod]
	public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
	{
		DateTime deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
			{
				throw new TimeoutException($"Timed out waiting for {what}.");
			}

			await Task.Delay(50);
		}
	}

	public static async Task<T> ForAsync<T>(Task<T> task, string what, TimeSpan? timeout = null)
	{
		try
		{
			return await task.WaitAsync(timeout ?? DefaultTimeout);
		}
		catch (TimeoutException)
		{
			throw new TimeoutException($"Timed out waiting for {what}.");
		}
	}
}
