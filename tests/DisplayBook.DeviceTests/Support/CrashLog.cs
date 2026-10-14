using Microsoft.Maui.LifecycleEvents;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// Writes every unhandled exception to %TEMP%\DisplayBook.DeviceTests.crash.log before the app
/// goes down. An exception escaping an async void handler ends the process with no test result
/// and, on Windows, nothing in the event log, so this file is the only record of why a run crashed.
/// </summary>
static class CrashLog
{
	public static string FilePath { get; } = Path.Combine(Path.GetTempPath(), "DisplayBook.DeviceTests.crash.log");

	public static MauiAppBuilder LogUnhandledExceptions(this MauiAppBuilder builder)
	{
		AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("AppDomain", e.ExceptionObject as Exception);
		TaskScheduler.UnobservedTaskException += (_, e) => Write("UnobservedTask", e.Exception);
#if WINDOWS
		builder.ConfigureLifecycleEvents(events => events.AddWindows(windows => windows.OnLaunching((application, _) =>
			application.UnhandledException += (_, e) => Write("WinUI", e.Exception))));
#endif
		return builder;
	}

	static void Write(string source, Exception? exception)
	{
		try
		{
			File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {source}: {exception}{Environment.NewLine}{Environment.NewLine}");
		}
		catch (IOException)
		{
			// Nothing more can be done while the process is going down.
		}
	}
}
