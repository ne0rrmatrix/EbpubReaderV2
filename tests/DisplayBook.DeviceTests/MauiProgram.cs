using CommunityToolkit.Maui;
using DeviceRunners.UITesting;
using DeviceRunners.VisualRunners;
using DisplayBook.App;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Services.BookMetadata;
using DisplayBook.App.Services.Opds;
using DisplayBook.App.Services.Sync;
using DisplayBook.DeviceTests.Support;
using Microsoft.Maui.Controls.Internals;

namespace DisplayBook.DeviceTests;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		MauiAppBuilder builder = MauiApp.CreateBuilder();
		builder
			// DisplayBook.App's own registrations: platform setup, services, view models, pages
			// and Shell routes, so pages under test are created exactly as the app creates them.
			.AddDisplayBook()
#pragma warning disable MCT001 // UseVisualTestRunner below calls UseMauiApp, which the analyzer can't see.
			.UseMauiCommunityToolkit()
#pragma warning restore MCT001
			// [UIFact]/[UITheory] tests run on the UI thread.
			.ConfigureUITesting()
			.UseVisualTestRunner(configuration => configuration
				// When dotnet test launches the app, this switches it to run every test straight
				// away, stream the results back over TCP and exit. Launched any other way (from the
				// IDE, or by hand on a device), the visual runner lists the tests to run by hand.
				.AddCliConfiguration()
				.AddConsoleResultChannel()
				.AddTestAssembly(typeof(MauiProgram).Assembly)
				.AddXunit());

		builder.LogUnhandledExceptions();
		ReplaceExternalDependencies(builder.Services);
		return builder.Build();
	}

	/// <summary>
	/// Swaps out the parts of the app that reach beyond the device: the network (every named
	/// HttpClient goes to an in-memory <see cref="FakeServer"/>), the system file pickers, mDNS
	/// discovery, and on-screen dialogs (answered by <see cref="TestDialogs"/>). Registered after
	/// AddDisplayBook, so these win over the app's own registrations.
	/// </summary>
	static void ReplaceExternalDependencies(IServiceCollection services)
	{
		services.AddHttpClient(OpdsConstants.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeServerHandler(FakeNetwork.Opds));
		services.AddHttpClient(SyncConstants.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeServerHandler(FakeNetwork.Firebase));
		services.AddHttpClient(BookMetadataConstants.GoogleBooksClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeServerHandler(FakeNetwork.GoogleBooks));
		services.AddHttpClient(BookMetadataConstants.OpenLibraryClientName).ConfigurePrimaryHttpMessageHandler(() => new FakeServerHandler(FakeNetwork.OpenLibrary));
		services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName).ConfigurePrimaryHttpMessageHandler(() => new FakeServerHandler(FakeNetwork.Default));

		services.AddSingleton<IBookPickerService, FakeBookPicker>();
		services.AddSingleton<IBonjourDiscoveryService, FakeBonjourDiscovery>();

		services.AddKeyedSingleton<Func<Page, AlertArguments, Task<bool>>>(TestDialogs.AlertKey, (_, _) => TestDialogs.OnAlertAsync);
		services.AddKeyedSingleton<Func<Page, ActionSheetArguments, Task<string>>>(TestDialogs.ActionSheetKey, (_, _) => TestDialogs.OnActionSheetAsync);
		services.AddKeyedSingleton<Func<Page, PromptArguments, Task<string>>>(TestDialogs.PromptKey, (_, _) => TestDialogs.OnPromptAsync);
	}
}
