using DeviceRunners.VisualRunners;

namespace DisplayBook.DeviceTests;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		MauiAppBuilder builder = MauiApp.CreateBuilder();
		builder.UseVisualTestRunner(configuration => configuration
			// When dotnet test launches the app, this switches it to run every test straight
			// away, stream the results back over TCP and exit. Launched any other way (from the
			// IDE, or by hand on a device), the visual runner lists the tests to run by hand.
			.AddCliConfiguration()
			.AddConsoleResultChannel()
			.AddTestAssembly(typeof(MauiProgram).Assembly)
			.AddXunit());

		return builder.Build();
	}
}
