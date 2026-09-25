namespace DisplayBook.App.Services;

/// <summary>A pending second-factor challenge returned by a sign-in attempt that needs a TOTP code.</summary>
public sealed record MfaChallenge(string PendingCredential, string EnrollmentId);

public sealed record FirebaseAuthResult(bool Succeeded, string? ErrorMessage, MfaChallenge? MfaChallenge = null)
{
	public static FirebaseAuthResult Success { get; } = new(true, null);
	public static FirebaseAuthResult Failure(string message) => new(false, message);
	public static FirebaseAuthResult NeedsMfa(MfaChallenge challenge) => new(false, null, challenge);
}

/// <summary>The signed-in account's verified/second-factor state, as reported by the server.</summary>
public sealed record AccountSecurityState(bool EmailVerified, string? TotpEnrollmentId, string? TotpDisplayName);

/// <summary>The in-progress state of a TOTP enrollment, before the user has entered a verification code.</summary>
public sealed record TotpEnrollmentStart(string SharedSecretKey, string SessionInfo, string OtpAuthUri);