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

All DI registration lives in `MauiProgram.cs` — there's no other composition root. Pages/viewmodels register with `AddTransientWithShellRoute<TPage, TViewModel>("route")` (or `AddSingletonWithShellRoute` for the Library root); this both registers the types in DI and calls `Routing.RegisterRoute` under the hood. Only `AppShell.xaml` declares the shell's root `ShellContent` — every other page is reached purely via `Shell.Current.GoToAsync("route")` (always `await`ed) called directly from the ViewModel's `[RelayCommand]` methods, with no XAML declaration at all and no navigation-wrapping service or interface of any kind. Data a target page needs travels as plain query-string parameters (e.g. `Shell.Current.GoToAsync($"Details?id={book.Id}")`), and the destination page implements `IQueryAttributable` to read it back and kick off its own async load (see `OpdsBookPage`/`ReaderPage`/`BookDetailsPage`) — never as an object passed through Shell's dictionary-parameter overload. New pages follow this pattern, not a XAML `ShellContent` entry.

`BookDetailsViewModel.cs` is compiled directly into `DisplayBook.Tests` (see "Tests" below) and so must stay MAUI-free; the handful of members that touch `Shell`/`Launcher` live in a second partial-class file, `BookDetailsViewModel.Platform.cs`, that the test `.csproj` does not include. If a ViewModel is ever both source-linked into tests and needs to navigate, use the same partial-class split rather than adding an abstraction over `Shell`.

External REST APIs (Google Books, Open Library, OPDS servers, Firebase) are wired as named `HttpClient`s via `AddHttpClient(name)` + `ConfigureHttpClient`, then wrapped in a hand-built `AddSingleton(sp => new XClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(name)))` — **not** `AddHttpClient<TInterface, TImpl>()`, which registers the client as transient and would break singleton state (e.g. cached auth tokens). Response JSON is parsed with `JsonDocument`/`JsonElement` (trim-safe, no reflection) rather than POCO deserialization, except where a small source-generated `JsonSerializerContext` (e.g. `ReaderJsonContext`, `AuthJsonContext`) is used for a fixed request/response shape.

### The EPUB reader: WebView + JS engine, not a native renderer

`EpubReaderView` (`DisplayBook.Viewer/Controls`) hosts a `WebView` running a hand-written JS reader (`Resources/Raw/DisplayBookViewer/EpubText.js` + `index.html`). Every chapter is assembled server-side, in C#, into **one combined HTML document** (`DisplayBook.Viewer/Services/CombinedDocumentBuilder.cs`), with each chapter's body wrapped in a `<section data-chapter-index="N" data-chapter-href="...">` that's `display:none` except for whichever chapter is current — JS never fetches or parses book content itself; it navigates the WebView to that one document exactly once per book and thereafter just toggles which `<section>` is visible (`showSection`/`goToSpineIndex` in `EpubText.js`). Paginated with pure CSS multi-column layout applied at the document root — page count is a function of viewport size/font/column settings, recomputed on every resize/settings change; hidden sections contribute zero layout width, so pagination naturally scopes to whichever chapter is visible without any extra clipping logic. There is **no persistent per-page DOM anchor**.

Native↔JS communication is a custom bridge, not a MAUI/JS interop RPC: JS calls `notifyNative(type, payload)`, which navigates the WebView to a `displaybook://bridge?message=...` URL that `EpubReaderView` intercepts and dispatches (`ReaderBridgeMessageTypes`: `shellReady`, `readerReady`, `locationChanged`, `readerError`, `themeChanged`, `requestExit`, `requestSettings`, `dictionaryLookupRequested`). Native→JS calls go the other way via `EvaluateJavaScriptAsync` against `window.DisplayBookReader = { setLocator, setSettings, clearSelection, getSelectionInfo, loadPublication }`. When touching either side of this contract, both `EpubText.js` and `EpubReaderView.xaml.cs` need matching changes — the payload shape is duck-typed JSON with no compiler check across the boundary. `EpubReaderView`'s "reader ready" handshake (`_pendingStartLocator`/`CompleteInitialReaderReady`) gates on the first `locationChanged` message matching the requested start locator by `ResourceHref` — get this wrong and the loading overlay never clears.

The reader shell (`index.html`/`EpubText.js`/the native WebView instance) is navigated to **at most once per app session**, not once per book (`EpubReaderView.EnsureReaderShellLoadedAsync`/`isShellReady`) — `ReaderPage`/`ReaderViewModel` are registered as DI singletons (`MauiProgram.cs`) specifically so the same `EpubReaderView` survives leaving and re-entering the reader. Opening book N+1 calls `window.DisplayBookReader.loadPublication(payload)` on the already-running JS instead of a fresh navigation. Any native-side per-WebView-instance wiring (event handlers, resource host bindings) must be idempotent across repeated calls for this reason — see the `DictionaryLookupBox`/`PublicationSourceBox` pattern in `ReaderAssetHost.windows.cs` for how to swap a mutable target instead of re-subscribing.

### EPUB content: parsed to memory, not extracted to disk

`BookImportService` persists exactly two things per book: the original `.epub` file (`BookStorageService.GetBookFilePath(bookId)`, i.e. `Books/{bookId}.epub`) and one small cover image extracted at import time (`Covers/{bookId}{ext}`). Nothing else is ever unzipped to disk. When a book is opened for reading, `ReaderViewModel.InitializeAsync` calls `EpubArchive.OpenAsync` (`DisplayBook.Viewer/Services/EpubArchive.cs`) to read every zip entry into an in-memory `Dictionary<string, byte[]>` once; `EpubReaderView.LoadPublicationAsync` then parses the OPF/spine/manifest/TOC (`EpubPublicationParser.Parse`) and assembles the combined reading document (`CombinedDocumentBuilder.Build`) from that same archive, storing the result back into it as a synthetic entry (`EpubArchive.SetSyntheticEntry`, under the reserved path `CombinedDocumentBuilder.CombinedDocumentPath` — never pick a manifest href that collides with it) so every platform's existing resource handler serves it exactly like a real archive entry. `EpubReaderView.PublicationSource` carries that archive into `ReaderAssetHost`, whose per-platform partials (`ReaderAssetHost.windows.cs`'s `WebResourceRequested`, `ReaderWebViewHandler.apple.cs`'s `ReaderContentSchemeHandler`, `ReaderAssetHost.android.cs`'s custom `WebViewAssetLoader.IPathHandler`) resolve every request against it instead of reading files from disk — unchanged by any of the above, since the combined document and every image/font/CSS request it makes still flow through the exact same `TryGetEntry` lookup as before. This exists specifically to avoid the per-file antivirus-scan/disk-I/O cost of opening dozens of small files right after a book opens.

`EpubPublicationParser` (`DisplayBook.Viewer/Services`, reader-time, operates on the in-memory `EpubArchive`) is a **separate, unrelated parser** from `EpubPackageReader.Read(ZipArchive)` (`DisplayBook.App/Services`, import-time-only, title/author/cover metadata, reads a `ZipArchive` directly over the `.epub` file) — don't merge them or assume one supersedes the other; they run at different times against different representations of the file for different purposes. `EpubPublicationParser` is the one that now owns spine/manifest/TOC parsing for the reader; `EpubPackageReader`'s narrower job is unchanged.

`BookSummary.EpubRelativePath` (backed by the `PublicationRoot` SQL column, kept under its original name to avoid churning `BookDatabase`'s positional-column mapping — see above) is now a relative path to that single file, not a folder. A one-time migration in `BookDatabase.MigrateLegacyExtractedBooksAsync` re-zips any pre-existing extracted-folder library entry into the new file format on first launch after this changed.

### Reading position ("locator")

`EpubLocator(ResourceHref, Page, PageCount, CharOffset)`: `ResourceHref` is the OPF-relative chapter href exactly as it appears in the spine (`EpubPublicationParser`'s `EpubSpineItem.Href`) — this exact string format is the cross-device sync key and must never change shape. `Page`/`PageCount` are the legacy, layout-dependent position (only meaningful for the current device's current pagination) kept for the "Page N of M" UI label; `CharOffset` is a device-independent character offset into the *current chapter's* text (computed via `caretRangeFromPoint`/`TreeWalker` over `SHOW_TEXT` nodes scoped to the active `<section>`, not the whole combined document — see `getActiveSectionElement` in `EpubText.js`) used to resume at the same point in the text regardless of screen size/font/pagination — this is what cross-device sync keys on. Persisted per-book in SQLite (`BookDatabase`) and, when signed in, mirrored to Firestore.

### Persistence: hand-written SQLite, positional columns

`BookDatabase` uses raw `Microsoft.Data.Sqlite` ADO.NET (no ORM). Schema migrations are additive columns applied via `EnsureColumnAsync` (checks `PRAGMA table_info`, then `ALTER TABLE ... ADD COLUMN` if missing) — there's no migration framework beyond this. `BookColumns`, the INSERT/SELECT column lists, `ReadBook`'s ordinal `reader.GetX(n)` calls, and `AddBookParameters` all encode the **same column order independently** — adding a column means updating all of these consistently (and appending a matching optional trailing parameter to the `BookSummary` record) or reads silently shift by one.

Two distinct book identifiers exist and are not interchangeable: `BookSummary.Id` is a random GUID minted per-device at import time (used as the local primary key and the on-disk `.epub` file name, `Books/{id}.epub` under `BookStorageService.ContentRoot`); `ContentHash` is a SHA-256 over the EPUB's zip-entry contents (relative-path-ordered, so deterministic across machines for a byte-identical source file even if the zip container/compression differs) used for local duplicate detection and as the cross-device sync key. Two devices importing non-byte-identical copies of "the same" book will get different `ContentHash`es and won't sync against each other.

Only the original `.epub` file (plus one small extracted cover image) is ever persisted to disk — see "EPUB content: parsed to memory, not extracted to disk" below. `BookImportService` never keeps the individual chapter/CSS/image/font files it reads while computing `ContentHash`/metadata.

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
