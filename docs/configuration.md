# Configuration and secrets

DisplayBook builds and runs with no configuration at all. Everything on this page is optional, and no secret is stored in the repository.

| Setting | What it's for | Without it |
| --- | --- | --- |
| [Firebase Web API key](#firebase-web-api-key) | Signing in for cross-device sync | Everything works except signing in |
| [Android signing keystore](#android-signing) | Signing Android builds with the app's own key | Android builds are signed with the SDK's debug key |
| [Google Books API key](#google-books-api-key) | Less-throttled Google Books metadata lookups | Google Books is used keyless (often throttled), with Open Library as the fallback |

## `Secrets.local.props`

Values that have to be built into the app live in `src/DisplayBook.App/Secrets.local.props`, which is gitignored. Create it from the tracked template:

```powershell
Copy-Item .\src\DisplayBook.App\Secrets.local.props.example .\src\DisplayBook.App\Secrets.local.props
```

```xml
<Project>
	<PropertyGroup>
		<FirebaseWebApiKeyEncoded></FirebaseWebApiKeyEncoded>
		<AndroidSigningKeyStore></AndroidSigningKeyStore>
		<AndroidSigningKeyAlias></AndroidSigningKeyAlias>
		<AndroidSigningStorePass></AndroidSigningStorePass>
		<AndroidSigningKeyPass></AndroidSigningKeyPass>
	</PropertyGroup>
</Project>
```

`DisplayBook.App.csproj` imports the file when it exists. Leave any value empty to go without it.

## Firebase Web API key

Signing in (Settings page) talks to Firebase Authentication and Firestore over REST, which needs the Firebase project's **Web API key**. The project id is in `src/DisplayBook.App/Services/Sync/FirebaseOptions.cs`; the key is not in the repository, so a fresh clone can't sign in until you supply one. To use your own Firebase project, see [Cross-device sync](sync.md#setting-up-your-own-firebase-project).

The key is supplied **base64-encoded**. That only keeps automated secret scanners from flagging it on every commit; it isn't encryption. A Firebase Web API key identifies the project rather than granting access: Firestore security rules and Firebase Authentication control access. Restrict the key in the Google Cloud Console (**APIs & Services → Credentials**) to the Identity Toolkit API and the Cloud Firestore API.

Encode it:

```powershell
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('your-web-api-key'))
```

Then:

- **Windows:** set the `MY_APP_SECRET` environment variable to the encoded value. The app reads it at runtime, so restart the app (and the terminal or IDE that launches it) after setting it.
- **Android, iOS and Mac Catalyst:** set `FirebaseWebApiKeyEncoded` in `Secrets.local.props` and rebuild. The value is built into the app as assembly metadata (`[AssemblyMetadata("FirebaseWebApiKeyEncoded", …)]`), because an app running on a device, simulator or emulator never sees your machine's environment variables.

## Android signing

By default, Android builds are signed with the Android SDK's debug key, like any .NET MAUI app. To sign with the app's own keystore, set these in `Secrets.local.props`:

| Property | Value |
| --- | --- |
| `AndroidSigningKeyStore` | The `.keystore` file: a name next to `Secrets.local.props`, or an absolute path. `*.keystore` is gitignored. |
| `AndroidSigningKeyAlias` | The key's alias. Defaults to `EpubReaderV2`. |
| `AndroidSigningStorePass` | The keystore password. |
| `AndroidSigningKeyPass` | The key password. |

## Google Books API key

Book metadata lookups (the details page's **Update metadata**) try Google Books, then Open Library. Google answers keyless requests but throttles them heavily, so a key helps. Create one in the [Google Cloud Console](https://console.cloud.google.com/) with the **Books API** enabled, then provide it in one of these ways, checked in this order:

1. The `DISPLAYBOOK_GOOGLE_BOOKS_API_KEY` environment variable. Windows only, in practice: environment variables don't reach an app running on a device or emulator.
2. A `GoogleBooksApiKey` entry in the app's `Preferences`, for a future settings option.
3. A file at `src/DisplayBook.App/Resources/Raw/googlebooks_apikey.txt` containing only the key. It is gitignored and bundled into the app, so it reaches every platform from one local setup.

## CI secrets

The GitHub Actions workflow (`.github/workflows/build.yml`) reads these repository secrets:

| Secret | Used for |
| --- | --- |
| `SONAR_TOKEN` | SonarQube Cloud analysis |
| `ANDROID_KEYSTORE_BASE64` | The Android keystore, base64-encoded; decoded to the runner's temp folder |
| `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_PASSWORD`, `ANDROID_KEY_ALIAS` | The keystore's passwords and alias |

Without the Android secrets, as on pull requests from forks, CI signs Android builds with the debug key.
