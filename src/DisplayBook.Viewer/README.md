# DisplayBook.Viewer

`DisplayBook.Viewer` is a reusable .NET MAUI class library for displaying reflowable EPUB publications in a WebView. It supports Windows and Android and provides reader navigation, themes, settings, table-of-contents navigation, and location events that a host application can use to save and restore reading progress.

The library is part of the DisplayBook repository and currently targets:

- `net10.0-windows10.0.19041.0`
- `net10.0-android`

## What the library provides

- `EpubReaderView`, a MAUI `ContentView` that hosts the EPUB reader.
- Platform-specific local content hosting for Windows and Android.
- A WebView reader implemented with bundled HTML, CSS, and JavaScript assets.
- `EpubLocator`, which identifies the current publication resource and page.
- Reader events for location changes, readiness, exit requests, settings requests, theme changes, and errors.
- `SetLocatorAsync` for moving the reader to a previously saved position.

The library renders EPUB content; it does not import EPUB files, maintain a book catalog, or provide persistence by itself. The host application is responsible for extracting EPUB files, storing publication metadata, and saving locations.

## Add the library to a MAUI application

For a local project reference, add the project to the solution and reference it from the application project:

```xml
<ItemGroup>
  <ProjectReference Include="..\DisplayBook.Viewer\DisplayBook.Viewer.csproj" />
</ItemGroup>
```

The library is not currently distributed as a NuGet package.

## Register the reader

Call `UseDisplayBookViewer()` while creating the MAUI application. This registers the platform-specific WebView handler and configures the Windows WebView2 data directory.

```csharp
using DisplayBook.Viewer;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder
			.UseMauiApp<App>()
			.UseDisplayBookViewer();

		return builder.Build();
	}
}
```

The host application must also include the normal .NET MAUI platform setup for the target platforms.

## Prepare EPUB content

The reader expects an extracted EPUB publication in the application's `ReaderContent` directory:

```text
<FileSystem.AppDataDirectory>
└── ReaderContent
	└── Books
		└── <book-id>
			├── META-INF
			├── OEBPS
			└── OEBPS\package.opf
```

The EPUB must be extracted before loading the reader. Do not pass the path to an `.epub` archive directly.

Two relative paths are required:

- `PublicationRoot`: the directory containing the extracted publication, relative to `ReaderContent`.
- `PublicationOpfPath`: the OPF file path, relative to the publication root.

For the structure above, use:

```csharp
PublicationRoot = "Books/my-book-id";
PublicationOpfPath = "OEBPS/package.opf";
```

Use forward slashes in relative paths. The library normalizes path separators when building the reader URL.

## Add the reader to a page

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage
	xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
	xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
	xmlns:viewer="clr-namespace:DisplayBook.Viewer.Controls;assembly=DisplayBook.Viewer"
	x:Class="SampleApp.ReaderPage">

	<viewer:EpubReaderView
		x:Name="Reader"
		PublicationRoot="Books/my-book-id"
		PublicationOpfPath="OEBPS/package.opf"
		HorizontalOptions="Fill"
		VerticalOptions="Fill" />
</ContentPage>
```

`EpubReaderView` loads the publication when the control is loaded. You can also call `LoadPublicationAsync` explicitly when the publication properties are assigned programmatically.

## Restore and save reading position

Use `EpubLocator` to restore a saved position and subscribe to `LocationChanged` to save future changes.

```csharp
using DisplayBook.Viewer.Models;

namespace SampleApp;

public partial class ReaderPage : ContentPage
{
	private readonly ReaderProgressStore _progressStore;
	private readonly string _bookId;

	public ReaderPage(
		string bookId,
		string publicationRoot,
		string publicationOpfPath,
		ReaderProgressStore progressStore)
	{
		InitializeComponent();

		_bookId = bookId;
		_progressStore = progressStore;
		Reader.PublicationRoot = publicationRoot;
		Reader.PublicationOpfPath = publicationOpfPath;
		Reader.StartLocator = _progressStore.Get(_bookId) ?? EpubLocator.Empty;

		Reader.LocationChanged += OnLocationChanged;
		Reader.ReaderError += OnReaderError;
	}

	private void OnLocationChanged(object? sender, EpubLocator locator)
	{
		_progressStore.Save(_bookId, locator);
	}

	private void OnReaderError(object? sender, string message)
	{
		// Display the error using the host application's UI and logging system.
	}

	protected override void OnDisappearing()
	{
		Reader.LocationChanged -= OnLocationChanged;
		Reader.ReaderError -= OnReaderError;
		base.OnDisappearing();
	}
}
```

`EpubLocator` contains:

```csharp
public sealed record EpubLocator(
	string ResourceHref,
	int Page,
	int PageCount);
```

`ResourceHref` identifies the current spine resource. `Page` is zero-based, and `PageCount` is the number of pages in that resource. Save all three values and pass them back through `StartLocator` when opening the same book again.

To move an already loaded reader to another position:

```csharp
await Reader.SetLocatorAsync(
	new EpubLocator(savedResourceHref, savedPage, savedPageCount),
	cancellationToken);
```

## Reader lifecycle

The normal startup sequence is:

1. Assign `PublicationRoot` and `PublicationOpfPath`.
2. Assign `StartLocator` if a saved position exists.
3. The control initializes the local asset host and loads the viewer page.
4. The viewer loads the publication and raises `ReaderReady`.
5. The saved locator is applied when supplied.
6. The control raises `LocationChanged` as the user moves through the publication.

Subscribe to events before or during page setup so the host does not miss location changes.

If the publication properties change after the control is loaded, the control reloads the publication. Set `StartLocator` to the desired position before the reload completes when changing books.

## Public API

### `EpubReaderView`

#### Properties

| Property | Type | Description |
| --- | --- | --- |
| `PublicationRoot` | `string` | Extracted publication directory relative to `ReaderContent`. |
| `PublicationOpfPath` | `string` | OPF path relative to `PublicationRoot`. |
| `StartLocator` | `EpubLocator` | Initial position to restore after the publication is loaded. Defaults to `EpubLocator.Empty`. |

#### Methods

| Method | Description |
| --- | --- |
| `LoadPublicationAsync(CancellationToken)` | Initializes the reader and loads the configured publication. Safe to call when the control has already loaded. |
| `SetLocatorAsync(EpubLocator, CancellationToken)` | Requests navigation to a resource and zero-based page in the loaded publication. |

#### Events

| Event | Payload | Description |
| --- | --- | --- |
| `ReaderReady` | `EventArgs` | Raised when the initial reader content and locator setup are ready. |
| `LocationChanged` | `EpubLocator` | Raised when the current resource or page changes. Use this to persist reading progress. |
| `ExitRequested` | `EventArgs` | Raised when the reader's exit/back action is requested. |
| `SettingsRequested` | `EventArgs` | Raised when the reader requests settings UI. |
| `ThemeChanged` | `string` | Raised when the active reader theme changes. The value is `paper`, `sepia`, or `night`. |
| `ReaderError` | `string` | Raised when the reader cannot load content or receives an invalid bridge message. |
| `MessageReceived` | `ReaderMessageEventArgs` | Raised for every raw reader bridge message. Most hosts should use the typed events instead. |

## Reader behavior

The bundled reader supports:

- Paged and continuous-scroll modes.
- Single- and two-column layouts where the viewport supports them.
- Paper, sepia, and night themes.
- Serif, sans, humanist, and monospace font families.
- Font size, line height, text alignment, hyphenation, paragraph spacing, indentation, word spacing, letter spacing, and font weight settings.
- Normal, dimmed, inverted, and dimmed-inverted image treatments.
- Keyboard navigation, tap navigation, swipe navigation, progress seeking, and table-of-contents navigation.

Reader settings are currently controlled by the bundled reader UI. `EpubReaderSettings` exposes the shared setting identifiers and defaults for hosts that need to interpret or integrate with those values.

## Platform requirements

### Windows

- Windows 10 build 19041 or later.
- WebView2 Runtime.
- The library maps the `displaybook.local` virtual host to its local reader content directory.

### Android

- Android API 21 or later.
- Android WebView with JavaScript enabled.
- The library serves content through Android WebView asset loading from application storage.

The host should not replace the reader WebView handler or disable JavaScript for the reader control.

## Build the library

From the repository root:

```powershell
dotnet restore .\DisplayBook.slnx
dotnet build .\src\DisplayBook.Viewer\DisplayBook.Viewer.csproj -c Debug
```

Build a specific target framework:

```powershell
dotnet build .\src\DisplayBook.Viewer\DisplayBook.Viewer.csproj `
  -f net10.0-windows10.0.19041.0 `
  -c Debug
```

The application project references this library and is useful as a working integration example.

## Troubleshooting

### The reader is blank

Verify all of the following:

- The extracted publication exists below the app's `ReaderContent` directory.
- `PublicationRoot` is relative to `ReaderContent`, not an absolute filesystem path.
- `PublicationOpfPath` points to an existing OPF file relative to `PublicationRoot`.
- The WebView control has been loaded before calling `LoadPublicationAsync`.
- The target platform's WebView runtime is installed.

### The book opens at the beginning

Ensure the host saves the latest `EpubLocator` received from `LocationChanged` and assigns it to `StartLocator` before opening the reader. Do not save only the page number; the resource href identifies the chapter or spine item.

### Reader errors are not visible

Subscribe to `ReaderError` and log or display the supplied message. Also verify that the host does not intercept `displaybook://bridge` navigation requests before the reader control receives them.

## License

No separate license file is currently included with this repository. Confirm licensing requirements before redistributing the library or bundled reader assets.
