using DisplayBook.App;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Views;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests;

/// <summary>DisplayBook.App's own startup: its composition root and its Application class.</summary>
public class AppStartupTests : AppDataTest
{
	[UIFact]
	public void CreateMauiApp_RegistersTheAppsServices()
	{
		using MauiApp app = MauiProgram.CreateMauiApp();

		Assert.NotNull(app.Services.GetService<IBookCatalogService>());
		Assert.NotNull(app.Services.GetService<IFirebaseAuthService>());
		Assert.NotNull(app.Services.GetService<IDownloadQueueService>());
	}

	[UIFact]
	public void App_LoadsItsResourcesAndOpensTheShell()
	{
		// Constructing an Application makes it Application.Current, which the test runner needs
		// back straight away.
		Application runner = Application.Current!;
		try
		{
			AppShell shell = new();
			DisplayBook.App.App app = new(shell);
			Assert.True(app.Resources.TryGetValue("Primary", out _));

			Window window = ((IApplication)app).CreateWindow(null!) as Window ?? throw new InvalidOperationException("No window.");
			Assert.Same(shell, window.Page);

			Window fallback = (Window)((IApplication)new DisplayBook.App.App(null!)).CreateWindow(null!);
			Assert.IsType<AppShell>(fallback.Page);
		}
		finally
		{
			Application.Current = runner;
		}
	}
}
