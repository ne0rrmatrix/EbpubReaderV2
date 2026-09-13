using System.Reflection;
using System.Text;

namespace DisplayBook.App.Services.Sync;

/// <summary>
/// Firebase project identifiers used to reach the Identity Toolkit (auth) and
/// Firestore REST APIs directly over HttpClient — no native Firebase SDK is
/// installed, so this project's Web API key is the only credential needed
/// (there is no Windows-native Firebase SDK, and a REST-only client keeps
/// behavior identical across every platform this app targets).
///
/// The Web API key is base64-encoded rather than a literal string, purely so
/// automated secret scanners (GitHub's included) stop flagging it on every commit.
/// This is obfuscation, not real secrecy — decoding it takes one line of code.
/// Per Google's own guidance, a Firebase Web API key isn't meant to be kept secret: it
/// identifies the project, it doesn't authorize access by itself. Actual access control
/// is enforced by the Firestore security rules (see firestore.rules) and Firebase
/// Authentication, and the key itself should be restricted in the Google Cloud Console
/// (APIs &amp; Services → Credentials) to only the Identity Toolkit API and Cloud
/// Firestore API, so even a copy of this key is far less useful outside this app.
///
/// The encoded value itself is never committed:
/// - Windows/Android read it from the MY_APP_SECRET environment variable at runtime.
/// - iOS/MacCatalyst don't reliably see host environment variables at runtime (an
///   Xcode/simulator launch doesn't inherit your shell's env), so instead it's baked
///   into the compiled assembly at build time from a local, gitignored
///   Secrets.local.props (see Secrets.local.props.example) via an &lt;AssemblyMetadata&gt;
///   MSBuild item, and read back here via reflection.
/// </summary>
public static class FirebaseOptions
{
	public const string ProjectId = "displaybook-sync";
	#if IOS || MACCATALYST
	static readonly string encodedWebApiKey = typeof(FirebaseOptions).Assembly
		.GetCustomAttributes<AssemblyMetadataAttribute>()
		.FirstOrDefault(a => a.Key == "FirebaseWebApiKeyEncoded")
		?.Value ?? string.Empty;
	#else
	static readonly string encodedWebApiKey = Environment.GetEnvironmentVariable("MY_APP_SECRET") ?? string.Empty;
	#endif
	public static string WebApiKey { get; } = Encoding.UTF8.GetString(Convert.FromBase64String(encodedWebApiKey));
}