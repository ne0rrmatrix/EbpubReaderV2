# Troubleshooting

## Building

### `MSB3021`: unable to copy `DisplayBook.App.exe`

The Windows app is still running from a previous build, which locks its `.exe`. Close it and build again, or build to another folder with `-o`.

### Android build or deployment fails

Check that the MAUI workload (`dotnet workload list`), the Android SDK and its required platforms, and an emulator or authorized device are all present. Visual Studio's Android tooling diagnostics can identify missing SDK components.

If `aapt2` fails with `APT2098` in a deeply nested folder (a git worktree, for example), the path is too long for Windows' 260-character limit. Enable long paths in Windows, or build from a shorter path (`subst X: C:\some\deep\folder`).

### iOS or Mac Catalyst build fails

On Windows, these targets compile but can't be packaged or run; that needs a Mac with Xcode. On the Mac, check that Xcode's command-line tools are selected (`xcode-select -p`) and that the MAUI workload is installed (`dotnet workload list`).

### `dotnet` reports the wrong SDK, or none

`global.json` pins the SDK to 10.0.401, rolling forward to later 10.0.4xx patches only. Install a matching SDK, and run `dotnet --version` from the repository folder to see which one it picks.

### Build output looks stale

```powershell
dotnet clean .\src\DisplayBook.App\DisplayBook.App.csproj
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -c Debug
```

If that doesn't help, delete the project's `bin` and `obj` folders. Reader asset changes (`src/DisplayBook.Viewer/Resources/Raw`) only take effect after a rebuild.

## Running the app

### The library is empty after importing a book

Check that the import finished without an error, then restart the app. The library is read from the SQLite catalog (`displaybook.db`), and the books themselves are stored separately under `ReaderContent/Books`.

### The reader doesn't show a book on Windows

Check that the WebView2 Runtime is installed and that the import finished successfully.

### Signing in on the Settings page fails

Without a Firebase Web API key, signing in always fails:

- **Windows:** set the `MY_APP_SECRET` environment variable, then restart the app and the terminal or IDE that launches it.
- **Android, iOS and Mac Catalyst:** set `FirebaseWebApiKeyEncoded` in `Secrets.local.props` and rebuild.

Both take the key base64-encoded. The value must be valid base64, or the app fails the first time it uses sync. See [Configuration and secrets](configuration.md#firebase-web-api-key).

### Metadata lookups fail or find nothing

Google Books throttles keyless requests heavily, so add a key (see [Configuration and secrets](configuration.md#google-books-api-key)). Open Library is tried as well and needs no key.

### OPDS discovery finds nothing on iOS or macOS

Both platforms need permission to use the local network, and the app must declare the Bonjour service it looks for. Both are declared in `Info.plist` (`NSLocalNetworkUsageDescription`, `NSBonjourServices`). Check that the local-network prompt was accepted (on iOS: **Settings → Privacy & Security → Local Network**), and test on a physical device, because mDNS in the iOS Simulator is unreliable. Calibre's server is plain HTTP, which `NSAllowsLocalNetworking` in `Info.plist` allows for local-network hosts only. If discovery still fails, add the server by URL. See [OPDS and Calibre](opds.md).

## Tests

### Device tests show a firewall prompt, or `dotnet test` waits forever

The test app reports results back over TCP port 16384. Allow it through the firewall on the first run. Always pass `-f` with a single target framework; see [Testing](testing.md#device-tests).

### The device test app crashes

Look in `%TEMP%\DisplayBook.DeviceTests.crash.log`. Under the coverage profiler, the page tests intermittently hit a native access violation in WinUI, and `tools/coverage.ps1` retries for that; see [Testing](testing.md#coverage).

### Reader tests can't find a browser

Run `npx playwright install chromium` in `tests/DisplayBook.ReaderTests` once.
