namespace DisplayBook.App.Services.Sync;

/// <summary>
/// Firebase project identifiers used to reach the Identity Toolkit (auth) and
/// Firestore REST APIs directly over HttpClient — no native Firebase SDK is
/// installed, so this project's Web API key is the only credential needed
/// (there is no Windows-native Firebase SDK, and a REST-only client keeps
/// behavior identical across every platform this app targets).
/// </summary>
public static class FirebaseOptions
{
    public const string ProjectId = "displaybook-sync";
    public const string WebApiKey = "AIzaSyCvj-QxkQl7mMa_IxxH2OLpyq9t8daJzsU";
}
