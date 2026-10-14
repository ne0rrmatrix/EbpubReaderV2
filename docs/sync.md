# Cross-device sync

Signing in on the Settings page keeps each book's reading position in sync across devices. Sign-in is by email and password, with optional two-factor authentication through an authenticator app (TOTP). This page explains how sync works and how to point the app at your own Firebase project.

## What is synced

Only reading positions. For each book you read, one document is stored in Cloud Firestore at `users/{uid}/positions/{contentHash}`, holding:

- the chapter (the spine item's href, exactly as the book's package lists it),
- a character offset into that chapter,
- the page and page count at the time (shown as "Page N of M"),
- when it was saved.

Books, covers, notes and settings never leave the device.

A book is identified by its **content hash**, a SHA-256 over the EPUB's file contents. Two devices therefore sync a book only if they imported byte-identical copies of it. The local book id can't be used: it is a random GUID minted on each device at import.

## How it works

- **No Firebase SDK.** Firebase Authentication (Identity Toolkit) and Firestore are called directly over REST with `HttpClient`. Windows has no first-party Firebase SDK, and REST keeps sign-in and sync identical on every platform.
- **Signing in.** `FirebaseAuthService` (`Services/Sync`) handles sign-up, sign-in, email verification, TOTP enrollment and challenges, and refresh-token renewal. The refresh token is kept in MAUI `SecureStorage`. If secure storage can't be read or written, for example after a backup restore, the app treats you as signed out instead of failing.
- **Pushing.** `PositionSyncService` pushes the position as you read. Pushes are debounced (about 4 seconds), since the reader reports a position on almost every page turn, and flushed when you leave the reader.
- **Pulling when a book opens.** Before a book opens, `ReaderViewModel` pulls the synced position (with a short timeout, so being offline doesn't hold the book up):
  - If this device has never read the book, or the two positions already match, it uses the synced one.
  - Otherwise it asks whether to jump to it, saying whether the synced position is newer or older than this device's.
- **Coming back to the app.** When the app returns to the foreground with a book open (`ReaderPage` watches the window's Activated and Resumed events), `CheckForNewerRemotePositionAsync` looks for a position synced since the one this device last saved or was asked about, and offers to jump there. Only one prompt is shown at a time.
- **Positions survive layout changes.** They resume by character offset, so the reader lands on the same text whatever the other device's screen size, font or column settings were; see [Architecture](architecture.md#reading-positions).
- **Access is per user.** The Firestore security rules (`firestore.rules`) allow each user to read and write only their own `users/{uid}/positions` documents.

## Setting up your own Firebase project

The repository points at the maintainer's Firebase project (`displaybook-sync`), but its Web API key isn't in the repository. To run sync against your own project:

1. Create a project in the [Firebase console](https://console.firebase.google.com/) (or `firebase projects:create`).
2. In **Project settings → General**, add a **Web app**. It is only a credential source for the REST APIs; nothing is deployed. Copy its **Web API key**.
3. Under **Build → Authentication**, select **Get started**, then on the **Sign-in method** tab enable **Email/Password**.
4. Under **Build → Firestore Database**, create a database in Native mode.
5. Optional, for two-factor authentication: under **Authentication → Sign-in method → Advanced → Multi-factor authentication**, enable **Authenticator apps (TOTP)**. This may prompt a free upgrade to Identity Platform; TOTP itself has no cost (only SMS-based MFA, which the app doesn't use, is billed).
6. Deploy the security rules and the Email/Password provider settings from this repository with the [Firebase CLI](https://firebase.google.com/docs/cli):

   ```powershell
   firebase login
   firebase use --add
   firebase deploy --only firestore,auth
   ```

   `firebase login` signs in to the Google account that owns the project; `firebase use --add` picks your project and gives it a local alias, so later commands target it instead of the maintainer's.

7. Set your project id in `src/DisplayBook.App/Services/Sync/FirebaseOptions.cs`:

   ```csharp
   public const string ProjectId = "your-project-id";
   ```

8. Give the app your Web API key, base64-encoded: `MY_APP_SECRET` on Windows, `FirebaseWebApiKeyEncoded` in `Secrets.local.props` elsewhere. See [Configuration and secrets](configuration.md#firebase-web-api-key), which also covers restricting the key.

The Firebase CLI project files are at the repository root: `firebase.json`, `.firebaserc`, `firestore.rules` and `firestore.indexes.json`.
