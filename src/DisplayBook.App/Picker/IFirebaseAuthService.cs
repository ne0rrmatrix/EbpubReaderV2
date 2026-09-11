namespace DisplayBook.App.Picker;

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

/// <summary>
/// Email/password auth (with optional TOTP second factor) against the Firebase Identity Toolkit
/// REST API. Deliberately not a native Firebase SDK: this keeps sign-in identical on Windows,
/// which has no first-party Firebase SDK, as on iOS/Android/macOS.
/// </summary>
public interface IFirebaseAuthService
{
	bool IsSignedIn { get; }
	string? CurrentEmail { get; }
	string? CurrentUserId { get; }

	/// <summary>Raised after sign-in, sign-up, or sign-out changes <see cref="IsSignedIn"/>.</summary>
	event EventHandler? AuthStateChanged;

	/// <summary>Restores any previously signed-in session from secure storage. Call once at startup.</summary>
	Task InitializeAsync(CancellationToken cancellationToken = default);

	Task<FirebaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default);

	/// <summary>
	/// Attempts email/password sign-in. If the account has a TOTP factor enrolled, this returns a
	/// non-succeeded result carrying <see cref="FirebaseAuthResult.MfaChallenge"/> instead of
	/// signing in — pass it to <see cref="CompleteMfaSignInAsync"/> with the user's code.
	/// </summary>
	Task<FirebaseAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default);

	/// <summary>Completes a sign-in that returned an <see cref="MfaChallenge"/>, given the user's TOTP code.</summary>
	Task<FirebaseAuthResult> CompleteMfaSignInAsync(MfaChallenge challenge, string code, CancellationToken cancellationToken = default);

	Task SignOutAsync();

	/// <summary>
	/// Returns a currently valid ID token (refreshing it first if needed), or null when
	/// signed out or the stored session could no longer be refreshed (e.g. revoked).
	/// </summary>
	Task<string?> TryGetValidIdTokenAsync(CancellationToken cancellationToken = default);

	/// <summary>Fetches the signed-in account's current verified/enrolled state from the server.</summary>
	Task<AccountSecurityState?> RefreshSecurityStateAsync(CancellationToken cancellationToken = default);

	/// <summary>Sends a verification email to the signed-in user (required before TOTP enrollment).</summary>
	Task<FirebaseAuthResult> SendEmailVerificationAsync(CancellationToken cancellationToken = default);

	/// <summary>Step one of TOTP enrollment: obtains a new shared secret to show the user.</summary>
	Task<TotpEnrollmentStart?> StartTotpEnrollmentAsync(CancellationToken cancellationToken = default);

	/// <summary>Step two of TOTP enrollment: confirms the user's authenticator app produces matching codes.</summary>
	Task<FirebaseAuthResult> FinalizeTotpEnrollmentAsync(string sessionInfo, string code, CancellationToken cancellationToken = default);

	/// <summary>Removes a previously enrolled TOTP factor.</summary>
	Task<FirebaseAuthResult> RemoveTotpEnrollmentAsync(string enrollmentId, CancellationToken cancellationToken = default);
}
