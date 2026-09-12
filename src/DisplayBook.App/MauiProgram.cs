using CommunityToolkit.Maui;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Picker;
using DisplayBook.App.Services;
using DisplayBook.App.Services.BookMetadata;
using DisplayBook.App.Services.Opds;
using DisplayBook.App.Services.Sync;
using DisplayBook.App.ViewModels;
using DisplayBook.App.Views;
using DisplayBook.Viewer;
using Microsoft.Extensions.Logging;

#if MAUI_DEVFLOW
using Microsoft.Maui.DevFlow.Agent;
#endif

namespace DisplayBook.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		MauiAppBuilder builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.UseDisplayBookViewer()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if MAUI_DEVFLOW
		builder.AddMauiDevFlowAgent();
#endif
		builder.Services.AddSingleton<AppShell>();
		builder.Services.AddSingleton<IBookCatalogService, BookCatalogService>();
		builder.Services.AddHttpClient(OpdsConstants.HttpClientName)
			.ConfigureHttpClient(client => client.Timeout = OpdsConstants.HttpClientTimeout);
		builder.Services.AddHttpClient<IOpdsParserService, OpdsParserService>(OpdsConstants.HttpClientName);
		builder.Services.AddSingleton<IBonjourDiscoveryService, BonjourDiscoveryService>();
		builder.Services.AddSingleton<IOpdsServerRepository, OpdsServerRepository>();
		builder.Services.AddSingleton<IOpdsCatalogCache, OpdsCatalogCache>();
		builder.Services.AddSingleton<IDownloadQueueService, DownloadQueueService>();
		builder.Services.AddSingleton<IBookDatabase, BookDatabase>();
		builder.Services.AddSingleton<IBookPickerService, BookPickerService>();
		builder.Services.AddSingleton<IBookImportService, BookImportService>();
		builder.Services.AddHttpClient(BookMetadataConstants.GoogleBooksClientName)
			.ConfigureHttpClient(client => client.Timeout = BookMetadataConstants.HttpClientTimeout);
		builder.Services.AddHttpClient(BookMetadataConstants.OpenLibraryClientName)
			.ConfigureHttpClient(client =>
			{
				client.Timeout = BookMetadataConstants.HttpClientTimeout;
				client.DefaultRequestHeaders.UserAgent.ParseAdd(BookMetadataConstants.OpenLibraryUserAgent);
			});
		builder.Services.AddSingleton(sp =>
			new GoogleBooksClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(BookMetadataConstants.GoogleBooksClientName)));
		builder.Services.AddSingleton(sp =>
			new OpenLibraryClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(BookMetadataConstants.OpenLibraryClientName)));
		builder.Services.AddSingleton<IBookMetadataService, BookMetadataService>();
		builder.Services.AddHttpClient(SyncConstants.HttpClientName)
			.ConfigureHttpClient(client => client.Timeout = SyncConstants.HttpClientTimeout);
		builder.Services.AddSingleton<IFirebaseAuthService>(sp =>
			new FirebaseAuthService(
				sp.GetRequiredService<IHttpClientFactory>().CreateClient(SyncConstants.HttpClientName),
				sp.GetRequiredService<ILogger<FirebaseAuthService>>()));
		builder.Services.AddSingleton<IPositionSyncService>(sp =>
			new PositionSyncService(
				sp.GetRequiredService<IHttpClientFactory>().CreateClient(SyncConstants.HttpClientName),
				sp.GetRequiredService<IFirebaseAuthService>(),
				sp.GetRequiredService<ILogger<PositionSyncService>>()));
		builder.Services.AddSingleton<INavigationService, NavigationService>();
		builder.Services.AddSingletonWithShellRoute<LibraryPage, LibraryViewModel>("library");
		builder.Services.AddSingleton<AppShell>();
		builder.Services.AddTransientWithShellRoute<BookDetailsPage, BookDetailsViewModel>("Details");
		builder.Services.AddTransientWithShellRoute<ReaderPage, ReaderViewModel>("reader");
		builder.Services.AddTransientWithShellRoute<OpdsServersPage, OpdsServersViewModel>("opds/servers");
		builder.Services.AddTransientWithShellRoute<OpdsCatalogPage, OpdsCatalogViewModel>("opds/catalog");
		builder.Services.AddTransientWithShellRoute<OpdsBookPage, OpdsBookViewModel>("opds/book");
		builder.Services.AddTransientWithShellRoute<DownloadsPage, DownloadsViewModel>("opds/downloads");
		builder.Services.AddTransientWithShellRoute<SettingsPage, SettingsViewModel>("settings");

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

}