# Architecture

DisplayBook is two projects: a .NET MAUI app, and a reusable reader control the app hosts.

- **`src/DisplayBook.App`** is the application: pages, view models, services, and platform code. It uses MVVM with CommunityToolkit.Mvvm's source generators (`[ObservableProperty]`, `[RelayCommand]`), CommunityToolkit.Maui, and `Microsoft.Data.Sqlite`.
- **`src/DisplayBook.Viewer`** is the EPUB reader: a MAUI control (`EpubReaderView`) hosting a WebView that runs a JavaScript reader engine. It is trimmable and AOT-clean (trim and AOT warnings are errors) and knows nothing about the app. Don't add app-only dependencies such as Firebase or OPDS to it. Its public API is documented in [The reader control](reader.md).

## The app project

| Folder | Contents |
| --- | --- |
| `Views` | Pages (library, book details, reader, OPDS servers, catalog and book, settings) and the download progress popup |
| `ViewModels` | One view model per page, plus the download centre and display models |
| `Interfaces` | Service interfaces (`DisplayBook.App.Interfaces`) |
| `Services` | Service implementations: import, storage, the SQLite catalog, metadata lookup, `Opds/` and `Sync/` |
| `Picker` | The file and folder pickers, one partial class per platform |
| `Models` | Records such as `BookSummary` and the OPDS models |
| `Platforms` | Each platform's entry points and platform-only code |
| `Resources` | Styles, colours, fonts, images (including the top-bar icons) and raw assets |

### Composition and navigation

All dependency injection registrations live in `MauiProgram.cs`, in its `AddDisplayBook()` extension; the device tests call the same extension, so their pages come from the same registrations. Pages and their view models are registered with `AddTransientWithShellRoute<TPage, TViewModel>("route")`, which also registers the Shell route. The library is a singleton, and so are the reader page and its view model, so the reader's WebView survives leaving and re-entering the reader (see [The reader engine](#the-reader-engine)).

Only `AppShell.xaml` declares a `ShellContent` (the library). Every other page is reached with `await Shell.Current.GoToAsync("route")`, called straight from view model commands; there is no navigation service. Data a page needs travels as query-string parameters (for example `Details?id={book.Id}`), and the page implements `IQueryAttributable` to read them and start its own load.

`BookDetailsViewModel.cs` is compiled into the unit-test project, so it must stay free of MAUI APIs. The members that touch `Shell` or `Launcher` are in a second partial file, `BookDetailsViewModel.Platform.cs`, which the tests leave out.

### Platform code

Platform-specific code lives in files named by platform (`*.android.cs`, `*.windows.cs`, `*.ios.cs`, `*.maccatalyst.cs`, `*.macios.cs` for both Apple platforms), not in `#if` blocks. `Directory.Build.targets` removes each platform's files from the other platforms' builds.

### HTTP, JSON and logging

- **Named HTTP clients.** Google Books, Open Library, OPDS servers and Firebase are named `HttpClient`s (`AddHttpClient(name)`), each wrapped in a hand-built singleton client. The typed `AddHttpClient<TInterface, TImpl>()` form isn't used, because it registers the client as transient and would lose singleton state such as cached auth tokens.
- **Trim-safe JSON.** Responses are read with `JsonDocument`/`JsonElement`. Fixed shapes are serialized with source-generated `System.Text.Json` contexts (`OpdsJsonContext`, `AuthJsonContext`, `SyncJsonContext`, `ReaderJsonContext`). Nothing relies on reflection-based serialization, so the code stays trim- and AOT-safe.
- **Generated logging.** Logging goes through source-generated `[LoggerMessage]` methods, in a nested `Log` class inside each class that logs.

## The reader engine

The reader is not a native renderer. `EpubReaderView` hosts a WebView running a hand-written JavaScript reader (`Resources/Raw/DisplayBookViewer/index.html` and `EpubText.js`).

### One document per book

When a book opens, the C# side combines all its chapters into **one HTML document** (`CombinedDocumentBuilder`). Each chapter's body is wrapped in a `<section data-chapter-index="N" data-chapter-href="…">`, and every section except the current one is `display:none`. The JavaScript loads that document once per book and then only switches which section is visible; it never fetches or parses book content itself.

Pagination is pure CSS multi-column layout on the document root, so the page count depends on the viewport, font and column settings and is recomputed on every resize or settings change. Hidden sections take no space, so pagination covers only the visible chapter.

### The bridge

The reader and the app talk over a custom bridge, not MAUI's JavaScript interop:

- **JavaScript to the app:** `notifyNative(type, payload)` navigates the WebView to `displaybook://bridge?message=…`, which `EpubReaderView` intercepts, cancels and dispatches.
- **The app to JavaScript:** `EvaluateJavaScriptAsync` calls functions on `window.DisplayBookReader`: `setLocator`, `setSettings`, `clearSelection`, `getSelectionInfo`, `setSafeAreaInsets`, `loadPublication` and `isReaderAlive`.

The JavaScript sends these messages (`ReaderBridgeMessageTypes`):

| Message | Meaning |
| --- | --- |
| `shellReady` | The reader page has loaded and can accept a book |
| `readerReady` | A book has opened |
| `locationChanged` | The reading position changed |
| `readerError` | Something failed |
| `themeChanged` | The theme changed |
| `chromeVisibilityChanged` | The toolbar was shown or hidden |
| `requestExit`, `requestSettings` | The reader's back and settings buttons |
| `dictionaryLookupRequested` | Look up the selected word |
| `openExternalLink` | Open an `http`, `https` or `mailto` link outside the reader (any other scheme is ignored) |

Payloads are untyped JSON, so a change to either side needs a matching change on the other. When a book opens, the loading screen stays up until the first `locationChanged` whose chapter matches the requested start position.

### One reader per session

The reader page (`index.html`, `EpubText.js` and the native WebView) is loaded **once per app session**, not once per book. The next book is handed to the running page with `window.DisplayBookReader.loadPublication(payload)`. For that reason, any per-WebView native wiring (event handlers, resource handlers) must tolerate being set up again. `ReaderAssetHost.windows.cs` swaps a mutable target (`PublicationSourceBox`, `DictionaryLookupBox`) rather than subscribing again.

## EPUB files: read in memory, never extracted

Each imported book is stored as exactly two files: its original `.epub` (`Books/{bookId}.epub`) and a small cover image extracted at import (`Covers/{bookId}{ext}`). Nothing else is ever unpacked to disk.

To open a book:

1. `ReaderViewModel` reads the whole `.epub` into memory once (`EpubArchive.OpenAsync`: every zip entry into a dictionary).
2. `EpubReaderView` parses the package, spine, manifest and table of contents (`EpubPublicationParser`).
3. It builds the combined document from the same archive (`CombinedDocumentBuilder`) and stores it back in the archive as a synthetic entry, under a reserved path (`CombinedDocumentBuilder.CombinedDocumentPath`).
4. Each platform's resource handler answers every WebView request from the archive:
   - Windows: `WebResourceRequested` in `ReaderAssetHost.windows.cs`, on the `displaybook.invalid` host.
   - Android: a `WebViewAssetLoader` path handler in `ReaderAssetHost.android.cs`.
   - iOS and Mac Catalyst: a custom URL scheme handler in `ReaderWebViewHandler.apple.cs`.

This avoids the disk and antivirus cost of opening dozens of small files as a book opens. `EpubArchivePrefetchCache` starts this work while the book's details page is showing, so the reader has less to do when the book is opened.

Two EPUB parsers exist on purpose:

- **`EpubPublicationParser`** (`DisplayBook.Viewer`) runs when a book is read, against the in-memory archive, and owns the spine and table of contents.
- **`EpubPackageReader`** (`DisplayBook.App`) runs only at import, reading title, author and cover straight from the `.epub` file.

They work at different times on different representations; don't merge them.

Libraries from older versions stored books as extracted folders. On first launch, `BookDatabase.MigrateLegacyExtractedBooksAsync` zips each such book back into a single `.epub`.

## Reading positions

A position is an `EpubLocator(ResourceHref, Page, PageCount, CharOffset)`:

- **`ResourceHref`** is the chapter's href exactly as the package's spine lists it. It is the cross-device sync key, so its format must never change.
- **`CharOffset`** is a character offset into the current chapter's text, found from the point on screen and counted over the text nodes of the visible `<section>` only. It finds the same place in the text whatever the screen size, font or pagination, so the reader resumes from it.
- **`Page` and `PageCount`** are layout-dependent and only meaningful on the device that reported them; they feed the "Page N of M" label and are the fallback when `CharOffset` is unknown.

Positions are saved per book in SQLite and, when signed in, synced; see [Cross-device sync](sync.md).

## Importing books

`BookImportService` imports a single file or a whole folder. Unpacked EPUB folders are zipped into `.epub` files on the way in. Each candidate is processed as follows:

1. It is copied to a staging location.
2. Its **content hash** is computed: a SHA-256 over its zip entries' contents in path order, so byte-identical books hash the same even when their zip containers differ.
3. If the library already holds that hash, the candidate is skipped as a duplicate.
4. Otherwise, its title, author and cover are read, the cover is extracted, the `.epub` is moved into the library, and the catalog row is added.

A folder import reports progress and can be cancelled part way. A candidate that fails is skipped, with the reason shown.

## Persistence

App data lives in the platform's app data folder (`FileSystem.AppDataDirectory`):

| Path | Contents |
| --- | --- |
| `displaybook.db` | The SQLite catalog: books, metadata, reading positions |
| `ReaderContent/Books/{id}.epub` | Imported books |
| `ReaderContent/Covers/{id}.{ext}` | Extracted covers |
| `ReaderContent/Opds/servers.json` | Saved OPDS servers (credentials in plain text; see [OPDS](opds.md#security-notes)) |
| `ReaderContent/Opds/Downloads/` | OPDS downloads in progress |
| `ReaderContent/Cache/` | The OPDS feed cache |

`BookDatabase` uses `Microsoft.Data.Sqlite` directly, with no ORM:

- **Additive migrations.** Schema changes add columns through `EnsureColumnAsync`, which checks `PRAGMA table_info` and runs `ALTER TABLE … ADD COLUMN` when a column is missing.
- **Column order is defined in four places.** The column lists, the `INSERT`/`SELECT` statements, `ReadBook`'s ordinal reads and `AddBookParameters` each encode the same order independently. Adding a column means updating all of them, and appending a matching optional parameter to the `BookSummary` record; otherwise reads silently shift by one.
- **Two identifiers.** A book has two, which aren't interchangeable:
  - `BookSummary.Id` is a random GUID minted at import. It is the local primary key and the file name.
  - `ContentHash` is used for duplicate detection and as the sync key.

## Metadata lookup

The details page's **Update metadata** asks `BookMetadataService` for better metadata. It tries Google Books (by ISBN, then by title and author), then Open Library the same way, and stops at the first match. The user picks which suggested fields to apply, and the last update can be undone (one level of undo).

## OPDS catalogs

`Services/Opds` contains:

- **`BonjourDiscoveryService`**: mDNS discovery of Calibre servers (`_calibre._tcp`).
- **`OpdsParserService`**: an Atom OPDS 1.0 and 2.0 parser.
- **`OpdsServerRepository`**: saved server profiles.
- **`OpdsCatalogCache`**: the feed cache.
- **`OpdsEntryStagingCache`**: keeps a tapped entry's metadata for the book page.
- **`DownloadQueueService`**: a resumable download queue, up to three downloads at a time, using HTTP range requests.

DisplayBook is a client only and never advertises itself. Finished downloads go through the same import as local files. The book page shows whether a book is already in the library by matching normalized title and author (`BookMetadataMatcher`, in `Services`). See [OPDS and Calibre](opds.md).

## Dictionary

The reader's word lookup uses `DictionaryLookupService` (`DisplayBook.Viewer`), backed by a bundled SQLite database of Webster's 1913 dictionary (`Resources/Raw/Dictionary/websters1913.db`). It is copied out of the app package into app data once before first use. `tools/DictionaryBuilder` regenerates it from Project Gutenberg's public-domain text; provenance and licensing are in `src/DisplayBook.Viewer/Resources/Raw/Dictionary/SOURCE.md`.
