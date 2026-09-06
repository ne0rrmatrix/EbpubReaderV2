using CommunityToolkit.Maui;
using DisplayBook.App.Services;
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
		var builder = MauiApp.CreateBuilder();
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
		builder.Services.AddSingleton<BookStorageService>();
		builder.Services.AddSingleton<IBookDatabase, BookDatabase>();
		builder.Services.AddSingleton<IBookPickerService, BookPickerService>();
		builder.Services.AddSingleton<IBookImportService, BookImportService>();
		builder.Services.AddSingleton<INavigationService, NavigationService>();
		builder.Services.AddSingletonWithShellRoute<LibraryPage, LibraryViewModel>("library");
		builder.Services.AddSingleton<AppShell>();
		builder.Services.AddTransientWithShellRoute<BookDetailsPage, BookDetailsViewModel>("Details");
		builder.Services.AddTransientWithShellRoute<ReaderPage, ReaderViewModel>("reader");

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

}
