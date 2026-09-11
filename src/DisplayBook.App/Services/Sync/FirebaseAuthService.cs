using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services.Sync;

public sealed partial class FirebaseAuthService(HttpClient httpClient, ILogger<FirebaseAuthService> logger) : IFirebaseAuthService, IDisposable
{
	const string idTokenKey = "idToken";
	const string refreshTokenInResponseKey = "refreshToken";
	const string signUpUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signUp";
	const string signInUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword";
	const string refreshUrl = "https://securetoken.googleapis.com/v1/token";
	const string lookupUrl = "https://identitytoolkit.googleapis.com/v1/accounts:lookup";
	const string sendOobCodeUrl = "https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode";
	const string mfaEnrollmentStartUrl = "https://identitytoolkit.googleapis.com/v2/accounts/mfaEnrollment:start";
	const string mfaEnrollmentFinalizeUrl = "https://identitytoolkit.googleapis.com/v2/accounts/mfaEnrollment:finalize";
	const string mfaEnrollmentWithdrawUrl = "https://identitytoolkit.googleapis.com/v2/accounts/mfaEnrollment:withdraw";
	const string mfaSignInFinalizeUrl = "https://identitytoolkit.googleapis.com/v2/accounts/mfaSignIn:finalize";
	string? totpDisplayName = "Authenticator app";

	const string refreshTokenKey = "displaybook.sync.refreshToken";
	const string userIdKey = "displaybook.sync.uid";
	const string emailKey = "displaybook.sync.email";

	readonly SemaphoreSlim refreshLock = new(1, 1);
	string? cachedIdToken;
	DateTimeOffset idTokenExpiresAt = DateTimeOffset.MinValue;
	bool initialized;
	bool disposedValue;

	public bool IsSignedIn { get; private set; }
	public string? CurrentEmail { get; private set; }
	public string? CurrentUserId { get; private set; }

	public event EventHandler? AuthStateChanged;

	public async Task InitializeAsync(CancellationToken cancellationToken = default)
	{
		if (initialized)
		{
			return;
		}

		initialized = true;
		string? refreshToken = await SecureStorage.Default.GetAsync(refreshTokenKey);
		if (string.IsNullOrWhiteSpace(refreshToken))
		{
			return;
		}

		CurrentUserId = await SecureStorage.Default.GetAsync(userIdKey);
		CurrentEmail = await SecureStorage.Default.GetAsync(emailKey);
		IsSignedIn = true;
	}

	public Task<FirebaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default) =>
		AuthenticateAsync(signUpUrl, email, password, cancellationToken);

	public Task<FirebaseAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default) =>
		AuthenticateAsync(signInUrl, email, password, cancellationToken);

	public async Task<FirebaseAuthResult> CompleteMfaSignInAsync(MfaChallenge challenge, string code, CancellationToken cancellationToken = default)
	{
		string url = $"{mfaSignInFinalizeUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
		var payload = new
		{
			mfaPendingCredential = challenge.PendingCredential,
			mfaEnrollmentId = challenge.EnrollmentId,
			totpVerificationInfo = new { verificationCode = code }
		};

		using HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, payload, cancellationToken);
		using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			return FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
		}

		JsonElement root = json.RootElement;
		string idToken = root.GetProperty(idTokenKey).GetString()!;
		string refreshToken = root.GetProperty(refreshTokenInResponseKey).GetString() ?? string.Empty;

		// mfaSignIn:finalize doesn't return localId/email/expiresIn (unlike signIn/signUp) — look the
		// account up with the fresh token to learn who just signed in.
		(string LocalId, string Email)? account = await LookupAccountAsync(idToken, cancellationToken);
		if (account is null)
		{
			return FirebaseAuthResult.Failure("Signed in, but the account details could not be loaded. Try again.");
		}

		await ApplySessionAsync(idToken, refreshToken, account.Value.LocalId, account.Value.Email, expiresInSeconds: 3600);
		return FirebaseAuthResult.Success;
	}

	public async Task SignOutAsync()
	{
		SecureStorage.Default.Remove(refreshTokenKey);
		SecureStorage.Default.Remove(userIdKey);
		SecureStorage.Default.Remove(emailKey);
		cachedIdToken = null;
		idTokenExpiresAt = DateTimeOffset.MinValue;
		IsSignedIn = false;
		CurrentEmail = null;
		CurrentUserId = null;
		AuthStateChanged?.Invoke(this, EventArgs.Empty);
		await Task.CompletedTask;
	}

	public async Task<string?> TryGetValidIdTokenAsync(CancellationToken cancellationToken = default)
	{
		if (!IsSignedIn)
		{
			return null;
		}

		if (cachedIdToken is not null && DateTimeOffset.UtcNow < idTokenExpiresAt)
		{
			return cachedIdToken;
		}

		await refreshLock.WaitAsync(cancellationToken);
		try
		{
			if (cachedIdToken is not null && DateTimeOffset.UtcNow < idTokenExpiresAt)
			{
				return cachedIdToken;
			}

			string? refreshToken = await SecureStorage.Default.GetAsync(refreshTokenKey);
			if (string.IsNullOrWhiteSpace(refreshToken))
			{
				await SignOutAsync();
				return null;
			}

			string url = $"{refreshUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
			using FormUrlEncodedContent content = new(new Dictionary<string, string>
			{
				["grant_type"] = "refresh_token",
				["refresh_token"] = refreshToken
			});

			using HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken);
			using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				logger.LogWarning("Could not refresh the sync session: {Error}", GetErrorMessage(json));
				await SignOutAsync();
				return null;
			}

			JsonElement root = json.RootElement;
			string idToken = root.GetProperty("id_token").GetString()!;
			string newRefreshToken = root.GetProperty("refresh_token").GetString()!;
			int expiresInSeconds = int.Parse(root.GetProperty("expires_in").GetString()!);

			await SecureStorage.Default.SetAsync(refreshTokenKey, newRefreshToken);
			cachedIdToken = idToken;
			// Refresh a little early so a call made right at expiry doesn't race the server's clock.
			idTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - 60));
			return idToken;
		}
		finally
		{
			refreshLock.Release();
		}
	}

	public async Task<AccountSecurityState?> RefreshSecurityStateAsync(CancellationToken cancellationToken = default)
	{
		string? idToken = await TryGetValidIdTokenAsync(cancellationToken);
		if (idToken is null)
		{
			return null;
		}

		try
		{
			string url = $"{lookupUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
			using HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, new { idToken }, cancellationToken);
			using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				logger.LogWarning("Could not refresh account security state: {Error}", GetErrorMessage(json));
				return null;
			}

			JsonElement user = json.RootElement.GetProperty("users")[0];
			bool emailVerified = user.TryGetProperty("emailVerified", out JsonElement verifiedElement) && verifiedElement.GetBoolean();

			string? totpEnrollmentId = null;
			totpDisplayName = null;
			if (user.TryGetProperty("mfaInfo", out JsonElement mfaInfo) && mfaInfo.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement factor in mfaInfo.EnumerateArray())
				{
					if (factor.TryGetProperty("totpInfo", out _))
					{
						totpEnrollmentId = factor.GetProperty("mfaEnrollmentId").GetString();
						totpDisplayName = factor.TryGetProperty("displayName", out JsonElement nameElement) ? nameElement.GetString() : null;
						break;
					}
				}
			}

			return new AccountSecurityState(emailVerified, totpEnrollmentId, totpDisplayName);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Could not refresh account security state.");
			return null;
		}
	}

	public async Task<FirebaseAuthResult> SendEmailVerificationAsync(CancellationToken cancellationToken = default)
	{
		string? idToken = await TryGetValidIdTokenAsync(cancellationToken);
		if (idToken is null)
		{
			return FirebaseAuthResult.Failure("You need to be signed in to verify your email.");
		}

		string url = $"{sendOobCodeUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
		using HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, new { requestType = "VERIFY_EMAIL", idToken }, cancellationToken);
		using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
		return response.IsSuccessStatusCode
			? FirebaseAuthResult.Success
			: FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
	}

	public async Task<TotpEnrollmentStart?> StartTotpEnrollmentAsync(CancellationToken cancellationToken = default)
	{
		string? idToken = await TryGetValidIdTokenAsync(cancellationToken);
		if (idToken is null)
		{
			return null;
		}

		string url = $"{mfaEnrollmentStartUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
		var payload = new { idToken, totpEnrollmentInfo = new { } };
		using HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, payload, cancellationToken);
		using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			logger.LogWarning("Could not start TOTP enrollment: {Error}", GetErrorMessage(json));
			return null;
		}

		JsonElement sessionInfoElement = json.RootElement.GetProperty("totpSessionInfo");
		string sharedSecretKey = sessionInfoElement.GetProperty("sharedSecretKey").GetString()!;
		string sessionInfo = sessionInfoElement.GetProperty("sessionInfo").GetString()!;
		string otpAuthUri = $"otpauth://totp/DisplayBook:{Uri.EscapeDataString(CurrentEmail ?? "account")}?secret={sharedSecretKey}&issuer=DisplayBook";

		return new TotpEnrollmentStart(sharedSecretKey, sessionInfo, otpAuthUri);
	}

	public async Task<FirebaseAuthResult> FinalizeTotpEnrollmentAsync(string sessionInfo, string code, CancellationToken cancellationToken = default)
	{
		string? idToken = await TryGetValidIdTokenAsync(cancellationToken);
		if (idToken is null)
		{
			return FirebaseAuthResult.Failure("You need to be signed in to finish enrollment.");
		}

		string url = $"{mfaEnrollmentFinalizeUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
		var payload = new
		{
			idToken,
			displayName = totpDisplayName,
			totpVerificationInfo = new { sessionInfo, verificationCode = code }
		};

		using HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, payload, cancellationToken);
		using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			return FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
		}

		JsonElement root = json.RootElement;
		string newIdToken = root.GetProperty(idTokenKey).GetString()!;
		string newRefreshToken = root.GetProperty(refreshTokenInResponseKey).GetString()!;
		if (CurrentUserId is { } uid && CurrentEmail is { } email)
		{
			await ApplySessionAsync(newIdToken, newRefreshToken, uid, email, expiresInSeconds: 3600);
		}

		return FirebaseAuthResult.Success;
	}

	public async Task<FirebaseAuthResult> RemoveTotpEnrollmentAsync(string enrollmentId, CancellationToken cancellationToken = default)
	{
		string? idToken = await TryGetValidIdTokenAsync(cancellationToken);
		if (idToken is null)
		{
			return FirebaseAuthResult.Failure("You need to be signed in to remove two-factor authentication.");
		}

		string url = $"{mfaEnrollmentWithdrawUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
		using HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, new { idToken, mfaEnrollmentId = enrollmentId }, cancellationToken);
		using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			return FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
		}

		JsonElement root = json.RootElement;
		string newIdToken = root.GetProperty(idTokenKey).GetString()!;
		string newRefreshToken = root.GetProperty(refreshTokenInResponseKey).GetString()!;
		if (CurrentUserId is { } uid && CurrentEmail is { } email)
		{
			await ApplySessionAsync(newIdToken, newRefreshToken, uid, email, expiresInSeconds: 3600);
		}

		return FirebaseAuthResult.Success;
	}

	async Task<FirebaseAuthResult> AuthenticateAsync(string endpoint, string email, string password, CancellationToken cancellationToken)
	{
		string url = $"{endpoint}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
		IdentityToolkitSignRequest request = new(email, password);

		using HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, request, AuthJsonContext.Default.IdentityToolkitSignRequest, cancellationToken);
		using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
		JsonElement root = json.RootElement;

		// A second-factor-required response is still HTTP 200 — it just carries a
		// pending credential instead of an idToken.
		if (response.IsSuccessStatusCode && root.TryGetProperty("mfaPendingCredential", out JsonElement pendingCredentialElement))
		{
			string? enrollmentId = FindTotpEnrollmentId(root);
			return enrollmentId is null
				? FirebaseAuthResult.Failure("This account requires a second factor that DisplayBook doesn't support yet.")
				: FirebaseAuthResult.NeedsMfa(new MfaChallenge(pendingCredentialElement.GetString()!, enrollmentId));
		}

		if (!response.IsSuccessStatusCode)
		{
			return FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
		}

		string idToken = root.GetProperty(idTokenKey).GetString()!;
		string refreshToken = root.GetProperty(refreshTokenInResponseKey).GetString()!;
		string localId = root.GetProperty("localId").GetString()!;
		int expiresInSeconds = int.Parse(root.GetProperty("expiresIn").GetString()!);

		await ApplySessionAsync(idToken, refreshToken, localId, email, expiresInSeconds);
		return FirebaseAuthResult.Success;
	}

	static string? FindTotpEnrollmentId(JsonElement signInResponseRoot)
	{
		if (!signInResponseRoot.TryGetProperty("mfaInfo", out JsonElement mfaInfo) || mfaInfo.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		foreach (JsonElement factor in mfaInfo.EnumerateArray())
		{
			if (factor.TryGetProperty("totpInfo", out _))
			{
				return factor.GetProperty("mfaEnrollmentId").GetString();
			}
		}

		return null;
	}

	async Task<(string LocalId, string Email)?> LookupAccountAsync(string idToken, CancellationToken cancellationToken)
	{
		try
		{
			string url = $"{lookupUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
			using HttpResponseMessage response = await httpClient.PostAsJsonAsync(url, new { idToken }, cancellationToken);
			using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				return null;
			}

			JsonElement user = json.RootElement.GetProperty("users")[0];
			return (user.GetProperty("localId").GetString()!, user.GetProperty("email").GetString()!);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Could not look up account details after MFA sign-in.");
			return null;
		}
	}

	async Task ApplySessionAsync(string idToken, string refreshToken, string localId, string email, int expiresInSeconds)
	{
		await SecureStorage.Default.SetAsync(refreshTokenKey, refreshToken);
		await SecureStorage.Default.SetAsync(userIdKey, localId);
		await SecureStorage.Default.SetAsync(emailKey, email);

		cachedIdToken = idToken;
		idTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - 60));
		CurrentUserId = localId;
		CurrentEmail = email;
		IsSignedIn = true;
		AuthStateChanged?.Invoke(this, EventArgs.Empty);
	}

	static string GetErrorMessage(JsonDocument json)
	{
		return json.RootElement.TryGetProperty("error", out JsonElement error) &&
			error.TryGetProperty("message", out JsonElement message)
			? message.GetString() ?? "Unknown error"
			: "Unknown error";
	}

	static string DescribeError(string code) => code switch
	{
		"EMAIL_EXISTS" => "An account with that email already exists.",
		"EMAIL_NOT_FOUND" or "INVALID_LOGIN_CREDENTIALS" or "INVALID_PASSWORD" => "Incorrect email or password.",
		"USER_DISABLED" => "This account has been disabled.",
		"TOO_MANY_ATTEMPTS_TRY_LATER" => "Too many attempts. Try again later.",
		"INVALID_TOTP_CODE" => "That code didn't match. Try again.",
		"INVALID_MFA_PENDING_CREDENTIAL" or "MFA_PENDING_CREDENTIAL_EXPIRED" => "That sign-in attempt expired. Start over.",
		"OPERATION_NOT_ALLOWED : TOTP based MFA not enabled." => "Two-factor authentication isn't enabled for this project yet.",
		"SECOND_FACTOR_EXISTS" => "That's already your enrolled authenticator.",
		_ when code.StartsWith("WEAK_PASSWORD", StringComparison.OrdinalIgnoreCase) => "Password should be at least 6 characters.",
		_ when code.Contains("UNVERIFIED_EMAIL", StringComparison.OrdinalIgnoreCase) => "Verify your email first, then try again.",
		_ when code.Contains("INVALID_CODE", StringComparison.OrdinalIgnoreCase) || code.Contains("TOTP", StringComparison.OrdinalIgnoreCase)
			=> "That code didn't match. Try again.",
		_ => $"Sign-in failed: {code}"
	};

	void Dispose(bool disposing)
	{
		if (!disposedValue)
		{
			if (disposing)
			{
				refreshLock.Dispose();
			}

			disposedValue = true;
		}
	}

	public void Dispose()
	{
		// Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}
