# DisplayBook documentation

## Using DisplayBook

- [User guide](user-guide.md): the library, reading and reader settings, the dictionary, sync, and what data is stored where.
- [OPDS and Calibre](opds.md): setting up a Calibre content server, browsing catalogs, and downloading books.

## Building and contributing

- [Getting started](getting-started.md): prerequisites, cloning, and running the app on Windows.
- [Building and running](building.md): every platform, release builds, and the shared build settings.
- [Configuration and secrets](configuration.md): the Firebase key, Android signing, the Google Books key, and CI secrets.
- [Testing](testing.md): the unit, device and browser test suites, coverage, CI, and checking for SonarQube Cloud issues before you push.
- [Contributing](contributing.md): workflow, code style, conventions, and adding icons.
- [Troubleshooting](troubleshooting.md): build, runtime and test problems.

## How it works

- [Architecture](architecture.md): the two projects, composition and navigation, the reader engine, in-memory EPUB handling, reading positions, persistence, importing, metadata, OPDS, and the dictionary.
- [The reader control](reader.md): `DisplayBook.Viewer`'s API, for hosting the reader in an app.
- [Cross-device sync](sync.md): how reading positions are synced, and setting up your own Firebase project.
