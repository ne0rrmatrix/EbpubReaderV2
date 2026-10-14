# Getting started

This guide takes you from a fresh clone to DisplayBook running on Windows, the one platform you can build and run without extra hardware. For Android, iOS and Mac Catalyst, see [Building and running](building.md).

## Prerequisites

| Tool | Needed for |
| --- | --- |
| [Git](https://git-scm.com/) | Cloning the repository |
| [.NET 10 SDK](https://dotnet.microsoft.com/download), 10.0.401 or a later 10.0.4xx patch (pinned in `global.json`) | Everything |
| The .NET MAUI workload (`dotnet workload install maui`) | Building the app |
| Visual Studio 2026 with the **.NET Multi-platform App UI development** workload, or VS Code with the C# Dev Kit and .NET MAUI extensions | Running and debugging from an IDE (optional; the command line works too) |
| [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | Running the Windows app (already installed on current Windows 10 and 11) |
| Android SDK, plus an emulator or a device with USB debugging | Android only |
| A Mac with Xcode | Packaging and running iOS and Mac Catalyst (on Windows those targets compile, which is enough for a solution build, but can't be packaged or run) |
| [Node.js](https://nodejs.org/) | The reader's browser tests only |
| [Firebase CLI](https://firebase.google.com/docs/cli) (`npm install -g firebase-tools`) | Setting up your own Firebase project for sync only |

Check the SDK:

```powershell
dotnet --version   # run in the repository: should print 10.0.4xx
```

## Clone and restore

```powershell
git clone https://github.com/ne0rrmatrix/EbpubReaderV2.git
cd EbpubReaderV2
dotnet restore .\DisplayBook.slnx
```

`dotnet build` restores automatically too; restoring first just makes package problems easier to spot.

## Build and run on Windows

```powershell
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -f net10.0-windows10.0.19041.0 -c Debug
.\src\DisplayBook.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\DisplayBook.App.exe
```

Or open `DisplayBook.slnx` in Visual Studio, choose **Windows Machine** as the target, and press **F5**.

The Windows app is unpackaged (`WindowsPackageType=None`), so the built `.exe` runs directly; there is no MSIX to install.

## What works without any setup

Everything except signing in for cross-device sync works on a fresh clone: importing and reading books, the library, metadata lookup (Open Library, and Google Books without a key) and OPDS catalogs. No secrets are stored in the repository. To sign in, sign Android builds with the app's own key, or use your own API keys, see [Configuration and secrets](configuration.md).

## Find your way around

```text
DisplayBook.slnx
├── src
│   ├── DisplayBook.App       The .NET MAUI app: pages, view models, services, platform code
│   └── DisplayBook.Viewer    The reusable EPUB reader control (a WebView plus a JavaScript reader)
├── tests
│   ├── DisplayBook.Tests          Unit tests (no MAUI)
│   ├── DisplayBook.DeviceTests    xUnit tests that run inside a MAUI app, including the real pages
│   └── DisplayBook.ReaderTests    Playwright tests for the JavaScript reader
├── tools
│   ├── coverage.ps1          Runs every test suite with coverage, as CI does
│   ├── sonar-precheck.ps1    Lists the C# and PowerShell issues SonarQube Cloud would report on a pull request
│   └── DictionaryBuilder     Builds the reader's offline dictionary database
├── docs                      This documentation
└── .github/workflows         CI: build, tests, coverage and SonarQube Cloud analysis
```

The repository root also holds the shared build settings (`Directory.Build.props`, `Directory.Build.targets`, `.editorconfig`, `global.json`) and the Firebase CLI project files (`firebase.json`, `.firebaserc`, `firestore.rules`, `firestore.indexes.json`).

## Next steps

- [Building and running](building.md) on every platform.
- [Architecture](architecture.md), for how the app and reader fit together.
- [Testing](testing.md) and [Contributing](contributing.md) before you open a pull request.
