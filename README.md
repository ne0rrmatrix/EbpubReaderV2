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
7. **[Firebase CLI](https://firebase.google.com/docs/cli)** (`npm install -g firebase-tools`), only if you want to set up your own Firebase project for the cross-device sync feature — see [Optional integrations](#optional-integrations) below. Not required to build or run the app.

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

## Optional integrations

The app builds and runs fully without either of these — they add metadata lookup and cross-device sync on top of the core reader.

### Google Books metadata lookup

When importing a book, DisplayBook can look up richer metadata (title, author, description, cover) from the Google Books API. This works without any setup — keyless requests are allowed — but Google throttles keyless traffic heavily, so lookups may fail or be rate-limited in practice.

To use your own API key:

1. In the [Google Cloud Console](https://console.cloud.google.com/), create or select a project, enable the **Books API**, and create an API key.
2. Provide the key to the app with one of the following (checked in this order, first match wins):
   - The `DISPLAYBOOK_GOOGLE_BOOKS_API_KEY` environment variable. Desktop/dev only — environment variables do not reach an Android or iOS build running on a device or emulator.
   - A local file at `src/DisplayBook.App/Resources/Raw/googlebooks_apikey.txt` containing just the key, with nothing else in it. This file is gitignored and bundled into the app package at build time, so it is the only option that reaches every platform (Windows, Android, iOS, Mac Catalyst) from one local setup.

No key works too — Open Library is used as a fallback metadata source and needs no key at all.

### Firebase sync (reading position across devices)

Signing in from the Settings page lets DisplayBook sync your reading position across devices (with optional TOTP two-factor authentication). It talks to Firebase Authentication and Cloud Firestore directly over REST — no native Firebase SDK is used, so behavior is identical on every platform, including Windows, which has no first-party Firebase SDK.

The app ships pointed at the maintainer's Firebase project. To use your own instead:

1. Create a Firebase project at [the Firebase console](https://console.firebase.google.com/) (or `firebase projects:create`).
2. In **Project settings → General**, add a **Web app**. This is only used as a credential source for the REST API — no web app is actually deployed. Copy the generated **Web API key**.
3. Under **Build → Authentication**, click **Get started** (first time only for a new project), then on the **Sign-in method** tab enable the **Email/Password** provider.
4. Under **Build → Firestore Database**, create a Firestore database (Native mode).
5. Optional — to support the app's two-factor authentication (TOTP): under **Authentication → Sign-in method → Advanced → Multi-factor authentication**, enable **Authenticator apps (TOTP)**. This may prompt a free upgrade to Identity Platform the first time; that's expected and has no cost implications for TOTP itself (only phone/SMS-based MFA, which this app doesn't use, is billed).
6. Point the Firebase CLI at your project and deploy the Firestore security rules and Email/Password provider config already checked into this repo (`firestore.rules`, `firebase.json`):

   ```powershell
   firebase login
   firebase use --add
   firebase deploy --only firestore,auth
   ```

   `firebase login` opens a browser to sign in with the Google account that owns (or has at least Editor access to) the Firebase project — it's a one-time step per machine, and the same command the Firebase CLI prerequisite in [Prerequisites](#prerequisites) refers to. `firebase use --add` then lets you pick that project from a list and give it a local alias (`default` is fine) so later `firebase` commands in this repo target it instead of the maintainer's project.

7. Update `src/DisplayBook.App/Services/Sync/FirebaseOptions.cs` with your project's values:

   ```csharp
   public const string ProjectId = "your-project-id";
   public const string WebApiKey = "your-web-api-key";
   ```

   A Firebase Web API key is not a secret in the way an OAuth client secret is — access to your data is enforced by the Firestore security rules and Authentication, not by hiding this value — so it's fine for this file to stay in source control.

Without this setup, the app still builds and runs normally; signing in on the Settings page will just fail until it points at a real Firebase project.

## Architecture overview

### DisplayBook.App

The application project contains the user-facing MAUI application:

- `Views` contains the library, book details, and reader pages.
- `ViewModels` contains MVVM state and commands.
- `Services` contains EPUB import, storage, SQLite catalog, navigation, and sync services.
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

### Cross-device sync

`Services\Sync` implements sign-in and reading-position sync against Firebase, entirely over REST (no native Firebase SDK), so it behaves identically on every supported platform. See [Firebase sync](#firebase-sync-reading-position-across-devices) above for setup.

- `FirebaseAuthService` handles email/password sign-up/sign-in, TOTP multi-factor authentication, and refresh-token renewal, persisting session state in MAUI `SecureStorage`.
- `PositionSyncService` pushes the current reading position to Firestore (debounced) and pulls the newest position — keyed by each book's content hash, not its local id, since the local id is randomly generated per device at import — before a book is opened, adopting it only if newer than the local save.

## Development notes

- The project uses nullable reference types.
- The application uses CommunityToolkit.Mvvm for observable properties and commands.
- The app uses CommunityToolkit.Maui and Microsoft.Data.Sqlite.
- The reader is a WebView-based EPUB renderer rather than a native document viewer.
- Do not add imported EPUB files to source control unless they are intentionally being used as test fixtures.
- `firebase.json`, `.firebaserc`, `firestore.rules`, and `firestore.indexes.json` at the repository root are Firebase CLI project files for the sync feature's Firestore rules and Email/Password provider config — see [Firebase sync](#firebase-sync-reading-position-across-devices) above.
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
