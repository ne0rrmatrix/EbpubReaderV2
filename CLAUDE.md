# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

DisplayBook is a cross-platform EPUB reader built with .NET MAUI (net10.0), targeting Windows, Android, iOS, and macOS (Mac Catalyst). It has a local library (SQLite-backed), a WebView-based EPUB reading engine, an OPDS/Calibre catalog browser, and (as of this writing) Firebase-backed cross-device reading-position sync. See `README.md` for full feature/prerequisite/troubleshooting detail — this file covers what's needed to work in the code productively.

## Commands

Restore (first time only; `dotnet build` restores automatically too):
```powershell
dotnet restore .\DisplayBook.slnx
```

Build a specific platform (Windows is the only one buildable without extra tooling on this machine):
```powershell
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -f net10.0-windows10.0.19041.0 -c Debug
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -f net10.0-android -c Debug
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -f net10.0-ios -c Debug          # compiles on Windows; full device/sim build needs a Mac
dotnet build .\src\DisplayBook.App\DisplayBook.App.csproj -f net10.0-maccatalyst -c Debug  # requires a Mac
```
Build everything: `dotnet build .\DisplayBook.slnx -c Debug`

Run tests (xUnit):
```powershell
dotnet test .\tests\DisplayBook.Tests\DisplayBook.Tests.csproj
dotnet test .\tests\DisplayBook.Tests\DisplayBook.Tests.csproj --filter "FullyQualifiedName~OpdsParserServiceTests"
dotnet test .\tests\DisplayBook.Tests\DisplayBook.Tests.csproj --filter "FullyQualifiedName~OpdsParserServiceTests.MethodName"
```

Run the Windows build directly (built exe, no packaging):
```
src\DisplayBook.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\DisplayBook.App.exe
```

## Architecture

### Two-project split

- **`DisplayBook.App`** — the MAUI application: Views/ViewModels (MVVM via `CommunityToolkit.Mvvm`, source-generated `[ObservableProperty]`/`[RelayCommand]`), Services (import, SQLite catalog, OPDS, metadata matching, navigation, sync), and per-platform folders (`Platforms/{iOS,Android,MacCatalyst,Windows}`, using MAUI's `.ios.cs`/`.android.cs`/`.maccatalyst.cs`/`.windows.cs` file-suffix multi-targeting rather than `#if PLATFORM` blocks in shared code).
- **`DisplayBook.Viewer`** — a separate, reusable, trim/AOT-clean MAUI control library (`IsTrimmable`, `EnableTrimAnalyzer`, `WarningsAsErrors` includes `IL2026;IL3050`) containing just the EPUB reader control. Never add App-only dependencies (Firebase, OPDS, etc.) here.

All DI registration lives in `MauiProgram.cs` — there's no other composition root. Pages/viewmodels register with `AddTransientWithShellRoute<TPage, TViewModel>("route")` (or `AddSingletonWithShellRoute` for the Library root); this both registers the types in DI and calls `Routing.RegisterRoute` under the hood. Only `AppShell.xaml` declares the shell's root `ShellContent` — every other page is reached purely via `Shell.Current.GoToAsync("route")` through `INavigationService`/`NavigationService`, with no XAML declaration at all. New pages follow this pattern, not a XAML `ShellContent` entry.

External REST APIs (Google Books, Open Library, OPDS servers, Firebase) are wired as named `HttpClient`s via `AddHttpClient(name)` + `ConfigureHttpClient`, then wrapped in a hand-built `AddSingleton(sp => new XClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(name)))` — **not** `AddHttpClient<TInterface, TImpl>()`, which registers the client as transient and would break singleton state (e.g. cached auth tokens). Response JSON is parsed with `JsonDocument`/`JsonElement` (trim-safe, no reflection) rather than POCO deserialization, except where a small source-generated `JsonSerializerContext` (e.g. `ReaderJsonContext`, `AuthJsonContext`) is used for a fixed request/response shape.

### The EPUB reader: WebView + JS engine, not a native renderer

`EpubReaderView` (`DisplayBook.Viewer/Controls`) hosts a `WebView` running a hand-written JS reader (`Resources/Raw/DisplayBookViewer/EpubText.js` + `index.html`). Chapters are loaded one at a time (`srcdoc`, fully re-parsed per chapter) and paginated with pure CSS multi-column layout — page count is a function of viewport size/font/column settings, recomputed on every resize/settings change; there is **no persistent per-page DOM anchor**.

Native↔JS communication is a custom bridge, not a MAUI/JS interop RPC: JS calls `notifyNative(type, payload)`, which navigates the WebView to a `displaybook://bridge?message=...` URL that `EpubReaderView` intercepts and dispatches (`ReaderBridgeMessageTypes`: `readerReady`, `locationChanged`, `readerError`, `themeChanged`, `requestExit`, `requestSettings`, `dictionaryLookupRequested`). Native→JS calls go the other way via `EvaluateJavaScriptAsync` against `window.DisplayBookReader = { setLocator, setSettings, clearSelection, getSelectionInfo }`. When touching either side of this contract, both `EpubText.js` and `EpubReaderView.xaml.cs` need matching changes — the payload shape is duck-typed JSON with no compiler check across the boundary. `EpubReaderView`'s "reader ready" handshake (`_pendingStartLocator`/`CompleteInitialReaderReady`) gates on the first `locationChanged` message matching the requested start locator by `ResourceHref` — get this wrong and the loading overlay never clears.

### Reading position ("locator")

`EpubLocator(ResourceHref, Page, PageCount, CharOffset)`: `Page`/`PageCount` are the legacy, layout-dependent position (only meaningful for the current device's current pagination) kept for the "Page N of M" UI label; `CharOffset` is a device-independent character offset into the chapter's text (computed via `caretRangeFromPoint`/`TreeWalker` over `SHOW_TEXT` nodes in `EpubText.js`) used to resume at the same point in the text regardless of screen size/font/pagination — this is what cross-device sync keys on. Persisted per-book in SQLite (`BookDatabase`) and, when signed in, mirrored to Firestore.

### Persistence: hand-written SQLite, positional columns

`BookDatabase` uses raw `Microsoft.Data.Sqlite` ADO.NET (no ORM). Schema migrations are additive columns applied via `EnsureColumnAsync` (checks `PRAGMA table_info`, then `ALTER TABLE ... ADD COLUMN` if missing) — there's no migration framework beyond this. `BookColumns`, the INSERT/SELECT column lists, `ReadBook`'s ordinal `reader.GetX(n)` calls, and `AddBookParameters` all encode the **same column order independently** — adding a column means updating all of these consistently (and appending a matching optional trailing parameter to the `BookSummary` record) or reads silently shift by one.

Two distinct book identifiers exist and are not interchangeable: `BookSummary.Id` is a random GUID minted per-device at import time (used as the local primary key and the on-disk folder name); `ContentHash` is a SHA-256 over the extracted EPUB's file bytes (relative-path-ordered, so deterministic across machines for a byte-identical source file) used for local duplicate detection and as the cross-device sync key. Two devices importing non-byte-identical copies of "the same" book will get different `ContentHash`es and won't sync against each other.

### OPDS/Calibre client

`Services/Opds` — mDNS/Bonjour discovery (`_calibre._tcp`), an Atom OPDS 1.0/2.0 feed parser, a server-profile repository, and a resumable download queue. This app is a client only; it never advertises itself as a server, and there's no reading-position-sync extension to OPDS (unrelated to the Firestore-based sync). See `docs/OPDS.md` for setup and security notes (server profiles, including credentials, are stored as plaintext JSON at `Opds/servers.json` — a known limitation, not a pattern to copy for anything sensitive).

### Cross-device sync (`Services/Sync`)

Firebase Auth (Identity Toolkit) + Firestore, called **directly over REST via `HttpClient`** — deliberately not a native Firebase SDK, so behavior (including sign-in) is identical on Windows (which has no first-party Firebase SDK) as on iOS/Android/macOS. `FirebaseAuthService` handles email/password sign-up/sign-in and refresh-token renewal, persisting the refresh token in MAUI `SecureStorage` (not `Preferences`, not a plaintext file). `PositionSyncService` pushes the current locator to `users/{uid}/positions/{contentHash}` (debounced ~4s, since the reader reports position on almost every page turn; flushed on leaving the reader page) and pulls the newest position before a book opens, adopting it only if newer than the local `LastOpenedAt` (last-write-wins). Firebase project id and Web API key live in `FirebaseOptions.cs`; Firestore security rules (`firestore.rules`) restrict each user's `positions` subcollection to that user's own `uid`.

### Tests: source-linked, not project-referenced

`tests/DisplayBook.Tests` does **not** reference `DisplayBook.App` (that would pull in MAUI). Instead its `.csproj` links specific source files directly via `<Compile Include="..\..\src\DisplayBook.App\...\Foo.cs" Link="..." />`. Pure logic you want covered (parsers, matchers, URL/naming normalizers, small services with no MAUI dependency) needs its `.cs` file added to that list explicitly, or it's invisible to the test project no matter how good the test is.

## Google Play Console operations

Use the `gplay` CLI for all Google Play Console operations. Discover commands with `gplay --help` and `gplay <command> --help`. Full reference: <https://github.com/tamtom/play-console-cli/blob/main/GPLAY.md>

## Project-specific constraint

Per `.github/copilot-instructions.md`: **do not modify navigation or Shell startup architecture when diagnosing a crash without explicit user approval** — prefer minimal, targeted diagnostic fixes and preserve existing navigation behavior.
