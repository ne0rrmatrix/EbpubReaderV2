# DisplayBook

[![Build](https://github.com/ne0rrmatrix/EbpubReaderV2/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/ne0rrmatrix/EbpubReaderV2/actions/workflows/build.yml)
[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=ne0rrmatrix_EbpubReaderV2&metric=alert_status)](https://sonarcloud.io/project/overview?id=ne0rrmatrix_EbpubReaderV2)

DisplayBook is an EPUB reader for Windows, Android, iOS and macOS. Keep your books in one library, read them the way you like, pull new ones from your Calibre server, and pick up where you left off on any of your devices.

## Features

### Your library

- Add EPUB files one at a time, or a whole folder at once. Duplicates are skipped automatically.
- See each book's cover, title, author and description, and fill in missing details from Google Books or Open Library with one tap.
- Search by title or author, sort by recently added, recently opened, title or author, and remove several books at once.

### Reading

- Paginated reading, in one or two columns, or a continuous scroll.
- Four themes: the book's original styling, Paper, Sepia and Night.
- Choose the font, size, line height, reading width, alignment, hyphenation, paragraph spacing and more. Dim or invert images for night reading.
- Turn pages by tapping, swiping or using the keyboard. Jump with the table of contents or the progress bar.
- Look up any word in the built-in dictionary, which works offline.
- Web and email links in a book open in your browser or mail app.
- DisplayBook remembers your place in every book, and keeps it even when you change the font or screen size.

### Calibre and OPDS catalogs

- Browse Calibre's content server, or any OPDS catalog, on your network. Servers are found automatically, or added by address.
- Download one book or a whole selection straight into your library, with progress, cancel and retry.

### Sync across devices (optional)

- Sign in with an email and password, with optional two-factor authentication through an authenticator app.
- Your reading position follows you: when another device has read further, DisplayBook offers to jump there, both when you open the book and when you come back to the app.
- Only reading positions are synced. Your books stay on your devices.

## Platforms

| Platform | Requires |
| --- | --- |
| Windows | Windows 10 (build 19041) or later, with the WebView2 Runtime |
| Android | Android 8.0 (API 26) or later |
| iOS | iOS 15 or later |
| macOS | macOS with Mac Catalyst 15 or later |

## Getting DisplayBook

There are no prebuilt downloads yet; DisplayBook is built from source. [Getting started](docs/getting-started.md) takes you from a fresh clone to the app running on Windows in a few commands, and [Building and running](docs/building.md) covers Android, iOS and macOS.

## Using DisplayBook

1. Add books with **Add EPUB** or **Add folder** in the library's top bar.
2. Select a book to see its details, then **Open book**.
3. Tap the middle of the page for the toolbar: the table of contents, reader settings, and the way back to your library.
4. To get books from Calibre, select **Browse OPDS** in the top bar.
5. To sync your place between devices, sign in on the **Settings** page.

The [user guide](docs/user-guide.md) covers everything in detail, and [OPDS and Calibre](docs/opds.md) explains how to set up a Calibre server for DisplayBook.

## Privacy

Your books, library and reading positions are stored on your device. Nothing is uploaded unless you sign in. When you are signed in, only reading positions are synced: which chapter, where in it, and when. See [Your data](docs/user-guide.md#your-data).

## For developers

DisplayBook is a .NET 10 MAUI app with a reusable, WebView-based EPUB reader control. The [documentation](docs/README.md) covers:

- [Getting started](docs/getting-started.md) and [Building and running](docs/building.md)
- [Configuration and secrets](docs/configuration.md): optional API keys and signing
- [Architecture](docs/architecture.md), [The reader control](docs/reader.md) and [Cross-device sync](docs/sync.md)
- [Testing](docs/testing.md), [Contributing](docs/contributing.md) and [Troubleshooting](docs/troubleshooting.md)

## License

DisplayBook is released under the [MIT License](LICENSE.txt). Copyright (c) 2026 James Crutchley.
