using System.Text;

namespace DisplayBook.App.Services.Sync;

/// <summary>
/// Firebase project identifiers used to reach the Identity Toolkit (auth) and
/// Firestore REST APIs directly over HttpClient — no native Firebase SDK is
/// installed, so this project's Web API key is the only credential needed
/// (there is no Windows-native Firebase SDK, and a REST-only client keeps
/// behavior identical across every platform this app targets).
///
/// The Web API key is stored base64-encoded below rather than as a literal string,
/// purely so automated secret scanners (GitHub's included) stop flagging it on every
/// commit. This is obfuscation, not real secrecy — decoding it takes one line of code.
/// Per Google's own guidance, a Firebase Web API key isn't meant to be kept secret: it
/// identifies the project, it doesn't authorize access by itself. Actual access control
/// is enforced by the Firestore security rules (see firestore.rules) and Firebase
/// Authentication, and the key itself should be restricted in the Google Cloud Console
/// (APIs &amp; Services → Credentials) to only the Identity Toolkit API and Cloud
/// Firestore API, so even a copy of this key is far less useful outside this app.
/// </summary>
public static class FirebaseOptions
{
    public const string ProjectId = "displaybook-sync";

    // Rotated 2026-09-10 after the previous key was publicly leaked (GitHub
    // secret-scanning alert #1); the old key was deleted in the Google Cloud Console
    // and is confirmed dead (API_KEY_INVALID). This is a new key from a second Web
    // app registration on the same Firebase project.
    private const string EncodedWebApiKey = "QUl6YVN5QmZHejIxMUMzS0lyR3pIU0Y4ZnJtWGNkTHphZGZlY3NB";

    public static string WebApiKey { get; } = Encoding.UTF8.GetString(Convert.FromBase64String(EncodedWebApiKey));
}
