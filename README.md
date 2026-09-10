# DisplayBook

DisplayBook is a cross-platform EPUB reader built with .NET MAUI. It provides a local library for importing EPUB books, book details and cover information, and a focused reading experience with pagination, themes, reader settings, table of contents navigation, and saved reading positions.

The project targets .NET 10 and runs on Windows, Android, iOS, and macOS (via Mac Catalyst).

## Features

- Import EPUB files or folders containing EPUB files.
- Browse books in a local library.
- View book covers, metadata, and descriptions.
- Read reflowable EPUB publications.
- Navigate by page, chapter, table of contents, or reading progress.
- Choose light, sepia, or night reading themes.
- Adjust reader settings such as font, font size, line spacing, alignment, and pagination mode.
- Save the current chapter and page so a book can resume where it was last opened.
- Store the library catalog and reading state locally using SQLite.
- Use the same reader control on Windows, Android, iOS, and macOS.
- Browse Calibre and other OPDS catalogs over the network, discovered automatically via mDNS or added by URL.
- Download books from OPDS catalogs into the local library with a resumable download queue.

## Solution structure

```text
DisplayBook.slnx
├── src
│   ├── DisplayBook.App
│   │   ├── .NET MAUI application
│   │   ├── Library, book details, and reader pages
│   │   ├── EPUB import and local catalog services
│   │   ├── OPDS/Calibre browsing and download services
│   │   └── Windows, Android, iOS, and macOS platform code
│   └── DisplayBook.Viewer
│       ├── Reusable .NET MAUI reader control library
│       ├── WebView-based EPUB rendering
│       └── Reader bridge and EPUB viewer assets
└── tests
    └── DisplayBook.Tests
        └── Unit tests for OPDS parsing and URL handling
```

## Supported platforms

| Platform | Target framework | Minimum platform version |
| --- | --- | --- |
| Windows | `net10.0-windows10.0.19041.0` | Windows 10 build 19041 |
| Android | `net10.0-android` | Android API 26 |
| iOS | `net10.0-ios` | iOS 15.0 |
| macOS (Mac Catalyst) | `net10.0-maccatalyst` | macOS via Mac Catalyst 15.0 |

The Windows app is configured as an unpackaged application (`WindowsPackageType=None`). Building iOS and Mac Catalyst requires a Mac with Xcode installed.

## Prerequisites

Install the following before building:

1. **Git**
2. **.NET 10 SDK**
3. **Visual Studio 2026** (Windows/Android) or **Visual Studio for Mac / Xcode command-line builds** (iOS/Mac Catalyst) with the following workloads:
   - .NET Multi-platform App UI development
   - .NET desktop development
   - Android SDK tools, if building for Android
4. **Android SDK and an emulator or connected device**, if running the Android target
5. **WebView2 Runtime**, if it is not already installed on Windows
6. **A Mac with Xcode installed**, if building for iOS or Mac Catalyst. iOS and Mac Catalyst cannot be built on Windows or Linux.

To verify the .NET SDK, open PowerShell and run:

```powershell
dotnet --version
```

The result should be a .NET 10 SDK version.

## Clone the repository

```powershell
git clone https://github.com/ne0rrmatrix/EbpubReaderV2.git
cd EbpubReaderV2
```

If you cloned the repository into a different directory, run all commands from that directory.

## Restore dependencies

Restore the solution before the first build:

```powershell
dotnet restore .\DisplayBook.slnx
```

NuGet packages are restored automatically by `dotnet build` as well, but running restore separately makes dependency problems easier to identify.

## Build the Windows app

Build the Windows target in Debug configuration:

```powershell
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj `
  -f net10.0-windows10.0.19041.0 `
  -c Debug
```

Build the complete solution instead:

```powershell
dotnet build .\DisplayBook.slnx -c Debug
```

The Windows executable is written below:

```text
src\DisplayBook.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\
```

You can also open `DisplayBook.slnx` in Visual Studio, select the Windows machine target, and press **F5**.

## Build and run Android

Make sure an Android emulator is running or an Android device is connected and authorized for debugging. List available MAUI devices with:

```powershell
dotnet maui device list
```

Build the Android target:

```powershell
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj `
  -f net10.0-android `
  -c Debug
```

To deploy and run from Visual Studio, select an Android emulator or device as the debug target and press **F5**.

Depending on the installed .NET MAUI workload and Android tooling, the exact device deployment command can vary. Visual Studio is the recommended way to launch the Android app for the first time.

## Build and run iOS

Building and running iOS targets requires a Mac with Xcode installed.

Build the iOS target (device build):

```bash
dotnet build src/DisplayBook.App/DisplayBook.App.csproj \
  -f net10.0-ios \
  -c Debug
```

To build and run on a booted iOS Simulator, pass the simulator's runtime identifier and UDID (list available simulators with `xcrun simctl list devices available`):

```bash
dotnet build src/DisplayBook.App/DisplayBook.App.csproj \
  -f net10.0-ios \
  -c Debug \
  -p:RuntimeIdentifier=iossimulator-arm64

xcrun simctl install <SIMULATOR_UDID> src/DisplayBook.App/bin/Debug/net10.0-ios/iossimulator-arm64/DisplayBook.App.app
xcrun simctl launch <SIMULATOR_UDID> com.ahdf.EpubReaderV2
```

To deploy to a physical device, a valid Apple Developer signing identity and provisioning profile are required. Visual Studio or Visual Studio Code with the .NET MAUI extension is the recommended way to select a device and launch the app.

## Build and run Mac Catalyst

Build the Mac Catalyst target:

```bash
dotnet build src/DisplayBook.App/DisplayBook.App.csproj \
  -f net10.0-maccatalyst \
  -c Debug
```

The built app bundle can be launched directly:

```bash
open src/DisplayBook.App/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/EpubReaderV2.app
```

## Use the app

1. Start DisplayBook.
2. Select **Add EPUB** to choose an EPUB file.
3. Alternatively, use the folder import option to import EPUB files from a directory.
4. Select a book in the library.
5. Open the book from its details page.
6. Use the reader controls, page navigation, table of contents, progress bar, and settings menu.
7. Leave the book and open it again. The reader should return to the last saved chapter and page.

Books and the SQLite catalog are stored in the platform's application data directory. The exact location depends on the operating system and app identity.

## Architecture overview

### DisplayBook.App

The application project contains the user-facing MAUI application:

- `Views` contains the library, book details, and reader pages.
- `ViewModels` contains MVVM state and commands.
- `Services` contains EPUB import, storage, SQLite catalog, and navigation services.
- `Platforms` contains platform-specific Windows, Android, iOS, and MacCatalyst behavior.

### DisplayBook.Viewer

The viewer project contains the reusable EPUB reader control:

- `EpubReaderView` hosts the WebView and communicates with the application.
- `Resources\Raw\DisplayBookViewer` contains the HTML, CSS, and JavaScript reader assets.
- The native/WebView bridge reports reader-ready, location, theme, settings, and exit events.

### Persistence

The app stores imported-book metadata and reading positions in a local SQLite database. Imported EPUB content is copied into the app's local storage so the reader can access it after the original file is moved or unavailable.

### OPDS / Calibre browsing

The app can browse remote OPDS catalogs (Calibre content servers) and download books into the local library. See [docs/OPDS.md](docs/OPDS.md) for setup, a sample server configuration, and security notes.

- `Services\Opds` contains mDNS discovery of Calibre servers (`_calibre._tcp`), an OPDS 1.0/2.0 Atom feed parser, the server-profile repository, a feed cache, and the resumable download queue.
- Discovered servers are found automatically on the local network; any OPDS feed can also be added manually by URL.
- Downloaded books are imported into the same local library as imported EPUBs.

## Development notes

- The project uses nullable reference types.
- The application uses CommunityToolkit.Mvvm for observable properties and commands.
- The app uses CommunityToolkit.Maui and Microsoft.Data.Sqlite.
- The reader is a WebView-based EPUB renderer rather than a native document viewer.
- Do not add imported EPUB files to source control unless they are intentionally being used as test fixtures.
- Unit tests live in `tests\DisplayBook.Tests` and cover the OPDS feed parser and URL normalizer:

```powershell
dotnet test .\tests\DisplayBook.Tests\DisplayBook.Tests.csproj
```

## Troubleshooting

### The library is empty after importing a book

Confirm that the import completed without an error and restart the app. The library is loaded from the local SQLite catalog, while EPUB content is stored separately in the app data directory.

### The reader does not display a book on Windows

Check that the WebView2 Runtime is installed and that the EPUB import completed successfully. Rebuild the app after changing reader assets under `src\DisplayBook.Viewer\Resources\Raw`.

### Android build or deployment fails

Verify that the Android workload, SDK, emulator/device, and required SDK platforms are installed. Visual Studio's Android tooling diagnostics can identify missing SDK components.

### iOS or Mac Catalyst build fails, or can only be built on a Mac

iOS and Mac Catalyst require Xcode and can only be built on macOS; there is no cross-compilation path from Windows or Linux. Verify Xcode is installed and its command-line tools are selected (`xcode-select -p`), and that the .NET MAUI iOS/Mac Catalyst workload components are installed (`dotnet workload list`).

### OPDS/Calibre discovery finds nothing on iOS or macOS

iOS and macOS require explicit permission to use the local network, and the app must declare the Bonjour service type it looks for. Both are already declared in `Info.plist` (`NSLocalNetworkUsageDescription`, `NSBonjourServices`); if discovery still finds nothing, confirm the local-network permission prompt was accepted (Settings → Privacy & Security → Local Network on iOS) and prefer testing on a physical device — mDNS discovery in the iOS Simulator is unreliable. Calibre's OPDS server is also plain HTTP, which iOS/macOS block by default; the `NSAppTransportSecurity` / `NSAllowsLocalNetworking` exception in `Info.plist` allows this for local-network hosts only.

### Build artifacts appear stale

Clean and rebuild the app:

```powershell
dotnet clean .\src\DisplayBook.App\DisplayBook.App.csproj
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -c Debug
```

## Contributing

1. Create a branch for your change.
2. Keep changes focused and follow the existing C# and XAML style.
3. Build the affected project before opening a pull request.
4. Test the change on each platform it affects.
5. Describe user-visible behavior and validation steps in the pull request.

## License

No license file is currently included in this repository. Contact the repository owner before redistributing the project or its assets.
