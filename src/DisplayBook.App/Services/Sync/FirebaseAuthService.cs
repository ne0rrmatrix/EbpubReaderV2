using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services.Sync;

public sealed class FirebaseAuthService(HttpClient httpClient, ILogger<FirebaseAuthService> logger) : IFirebaseAuthService
{
    private const string SignUpUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signUp";
    private const string SignInUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword";
    private const string RefreshUrl = "https://securetoken.googleapis.com/v1/token";
    private const string LookupUrl = "https://identitytoolkit.googleapis.com/v1/accounts:lookup";
    private const string SendOobCodeUrl = "https://identitytoolkit.googleapis.com/v1/accounts:sendOobCode";
    private const string MfaEnrollmentStartUrl = "https://identitytoolkit.googleapis.com/v2/accounts/mfaEnrollment:start";
    private const string MfaEnrollmentFinalizeUrl = "https://identitytoolkit.googleapis.com/v2/accounts/mfaEnrollment:finalize";
    private const string MfaEnrollmentWithdrawUrl = "https://identitytoolkit.googleapis.com/v2/accounts/mfaEnrollment:withdraw";
    private const string MfaSignInFinalizeUrl = "https://identitytoolkit.googleapis.com/v2/accounts/mfaSignIn:finalize";
    private const string TotpDisplayName = "Authenticator app";

    private const string RefreshTokenKey = "displaybook.sync.refreshToken";
    private const string UserIdKey = "displaybook.sync.uid";
    private const string EmailKey = "displaybook.sync.email";

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedIdToken;
    private DateTimeOffset _idTokenExpiresAt = DateTimeOffset.MinValue;
    private bool _initialized;

    public bool IsSignedIn { get; private set; }
    public string? CurrentEmail { get; private set; }
    public string? CurrentUserId { get; private set; }

    public event EventHandler? AuthStateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        CurrentUserId = await SecureStorage.Default.GetAsync(UserIdKey);
        CurrentEmail = await SecureStorage.Default.GetAsync(EmailKey);
        IsSignedIn = true;
    }

    public Task<FirebaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default) =>
        AuthenticateAsync(SignUpUrl, email, password, cancellationToken);

    public Task<FirebaseAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default) =>
        AuthenticateAsync(SignInUrl, email, password, cancellationToken);

    public async Task<FirebaseAuthResult> CompleteMfaSignInAsync(MfaChallenge challenge, string code, CancellationToken cancellationToken = default)
    {
        var url = $"{MfaSignInFinalizeUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
        var payload = new
        {
            mfaPendingCredential = challenge.PendingCredential,
            mfaEnrollmentId = challenge.EnrollmentId,
            totpVerificationInfo = new { verificationCode = code }
        };

        using var response = await httpClient.PostAsJsonAsync(url, payload, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
        }

        var root = json.RootElement;
        var idToken = root.GetProperty("idToken").GetString()!;
        var refreshToken = root.GetProperty("refreshToken").GetString()!;

        // mfaSignIn:finalize doesn't return localId/email/expiresIn (unlike signIn/signUp) — look the
        // account up with the fresh token to learn who just signed in.
        var account = await LookupAccountAsync(idToken, cancellationToken);
        if (account is null)
        {
            return FirebaseAuthResult.Failure("Signed in, but the account details could not be loaded. Try again.");
        }

        await ApplySessionAsync(idToken, refreshToken, account.Value.LocalId, account.Value.Email, expiresInSeconds: 3600);
        return FirebaseAuthResult.Success;
    }

    public async Task SignOutAsync()
    {
        SecureStorage.Default.Remove(RefreshTokenKey);
        SecureStorage.Default.Remove(UserIdKey);
        SecureStorage.Default.Remove(EmailKey);
        _cachedIdToken = null;
        _idTokenExpiresAt = DateTimeOffset.MinValue;
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

        if (_cachedIdToken is not null && DateTimeOffset.UtcNow < _idTokenExpiresAt)
        {
            return _cachedIdToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedIdToken is not null && DateTimeOffset.UtcNow < _idTokenExpiresAt)
            {
                return _cachedIdToken;
            }

            var refreshToken = await SecureStorage.Default.GetAsync(RefreshTokenKey);
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                await SignOutAsync();
                return null;
            }

            var url = $"{RefreshUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            });

            using var response = await httpClient.PostAsync(url, content, cancellationToken);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Could not refresh the sync session: {Error}", GetErrorMessage(json));
                await SignOutAsync();
                return null;
            }

            var root = json.RootElement;
            var idToken = root.GetProperty("id_token").GetString()!;
            var newRefreshToken = root.GetProperty("refresh_token").GetString()!;
            var expiresInSeconds = int.Parse(root.GetProperty("expires_in").GetString()!);

            await SecureStorage.Default.SetAsync(RefreshTokenKey, newRefreshToken);
            _cachedIdToken = idToken;
            // Refresh a little early so a call made right at expiry doesn't race the server's clock.
            _idTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - 60));
            return idToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<AccountSecurityState?> RefreshSecurityStateAsync(CancellationToken cancellationToken = default)
    {
        var idToken = await TryGetValidIdTokenAsync(cancellationToken);
        if (idToken is null)
        {
            return null;
        }

        try
        {
            var url = $"{LookupUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
            using var response = await httpClient.PostAsJsonAsync(url, new { idToken }, cancellationToken);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Could not refresh account security state: {Error}", GetErrorMessage(json));
                return null;
            }

            var user = json.RootElement.GetProperty("users")[0];
            var emailVerified = user.TryGetProperty("emailVerified", out var verifiedElement) && verifiedElement.GetBoolean();

            string? totpEnrollmentId = null;
            string? totpDisplayName = null;
            if (user.TryGetProperty("mfaInfo", out var mfaInfo) && mfaInfo.ValueKind == JsonValueKind.Array)
            {
                foreach (var factor in mfaInfo.EnumerateArray())
                {
                    if (factor.TryGetProperty("totpInfo", out _))
                    {
                        totpEnrollmentId = factor.GetProperty("mfaEnrollmentId").GetString();
                        totpDisplayName = factor.TryGetProperty("displayName", out var nameElement) ? nameElement.GetString() : null;
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
        var idToken = await TryGetValidIdTokenAsync(cancellationToken);
        if (idToken is null)
        {
            return FirebaseAuthResult.Failure("You need to be signed in to verify your email.");
        }

        var url = $"{SendOobCodeUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
        using var response = await httpClient.PostAsJsonAsync(url, new { requestType = "VERIFY_EMAIL", idToken }, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return response.IsSuccessStatusCode
            ? FirebaseAuthResult.Success
            : FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
    }

    public async Task<TotpEnrollmentStart?> StartTotpEnrollmentAsync(CancellationToken cancellationToken = default)
    {
        var idToken = await TryGetValidIdTokenAsync(cancellationToken);
        if (idToken is null)
        {
            return null;
        }

        var url = $"{MfaEnrollmentStartUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
        var payload = new { idToken, totpEnrollmentInfo = new { } };
        using var response = await httpClient.PostAsJsonAsync(url, payload, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Could not start TOTP enrollment: {Error}", GetErrorMessage(json));
            return null;
        }

        var sessionInfoElement = json.RootElement.GetProperty("totpSessionInfo");
        var sharedSecretKey = sessionInfoElement.GetProperty("sharedSecretKey").GetString()!;
        var sessionInfo = sessionInfoElement.GetProperty("sessionInfo").GetString()!;
        var otpAuthUri = $"otpauth://totp/DisplayBook:{Uri.EscapeDataString(CurrentEmail ?? "account")}?secret={sharedSecretKey}&issuer=DisplayBook";

        return new TotpEnrollmentStart(sharedSecretKey, sessionInfo, otpAuthUri);
    }

    public async Task<FirebaseAuthResult> FinalizeTotpEnrollmentAsync(string sessionInfo, string code, CancellationToken cancellationToken = default)
    {
        var idToken = await TryGetValidIdTokenAsync(cancellationToken);
        if (idToken is null)
        {
            return FirebaseAuthResult.Failure("You need to be signed in to finish enrollment.");
        }

        var url = $"{MfaEnrollmentFinalizeUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
        var payload = new
        {
            idToken,
            displayName = TotpDisplayName,
            totpVerificationInfo = new { sessionInfo, verificationCode = code }
        };

        using var response = await httpClient.PostAsJsonAsync(url, payload, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
        }

        var root = json.RootElement;
        var newIdToken = root.GetProperty("idToken").GetString()!;
        var newRefreshToken = root.GetProperty("refreshToken").GetString()!;
        if (CurrentUserId is { } uid && CurrentEmail is { } email)
        {
            await ApplySessionAsync(newIdToken, newRefreshToken, uid, email, expiresInSeconds: 3600);
        }

        return FirebaseAuthResult.Success;
    }

    public async Task<FirebaseAuthResult> RemoveTotpEnrollmentAsync(string enrollmentId, CancellationToken cancellationToken = default)
    {
        var idToken = await TryGetValidIdTokenAsync(cancellationToken);
        if (idToken is null)
        {
            return FirebaseAuthResult.Failure("You need to be signed in to remove two-factor authentication.");
        }

        var url = $"{MfaEnrollmentWithdrawUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
        using var response = await httpClient.PostAsJsonAsync(url, new { idToken, mfaEnrollmentId = enrollmentId }, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
        }

        var root = json.RootElement;
        var newIdToken = root.GetProperty("idToken").GetString()!;
        var newRefreshToken = root.GetProperty("refreshToken").GetString()!;
        if (CurrentUserId is { } uid && CurrentEmail is { } email)
        {
            await ApplySessionAsync(newIdToken, newRefreshToken, uid, email, expiresInSeconds: 3600);
        }

        return FirebaseAuthResult.Success;
    }

    private async Task<FirebaseAuthResult> AuthenticateAsync(string endpoint, string email, string password, CancellationToken cancellationToken)
    {
        var url = $"{endpoint}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
        var request = new IdentityToolkitSignRequest(email, password);

        using var response = await httpClient.PostAsJsonAsync(url, request, AuthJsonContext.Default.IdentityToolkitSignRequest, cancellationToken);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = json.RootElement;

        // A second-factor-required response is still HTTP 200 — it just carries a
        // pending credential instead of an idToken.
        if (response.IsSuccessStatusCode && root.TryGetProperty("mfaPendingCredential", out var pendingCredentialElement))
        {
            var enrollmentId = FindTotpEnrollmentId(root);
            if (enrollmentId is null)
            {
                return FirebaseAuthResult.Failure("This account requires a second factor that DisplayBook doesn't support yet.");
            }

            return FirebaseAuthResult.NeedsMfa(new MfaChallenge(pendingCredentialElement.GetString()!, enrollmentId));
        }

        if (!response.IsSuccessStatusCode)
        {
            return FirebaseAuthResult.Failure(DescribeError(GetErrorMessage(json)));
        }

        var idToken = root.GetProperty("idToken").GetString()!;
        var refreshToken = root.GetProperty("refreshToken").GetString()!;
        var localId = root.GetProperty("localId").GetString()!;
        var expiresInSeconds = int.Parse(root.GetProperty("expiresIn").GetString()!);

        await ApplySessionAsync(idToken, refreshToken, localId, email, expiresInSeconds);
        return FirebaseAuthResult.Success;
    }

    private static string? FindTotpEnrollmentId(JsonElement signInResponseRoot)
    {
        if (!signInResponseRoot.TryGetProperty("mfaInfo", out var mfaInfo) || mfaInfo.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var factor in mfaInfo.EnumerateArray())
        {
            if (factor.TryGetProperty("totpInfo", out _))
            {
                return factor.GetProperty("mfaEnrollmentId").GetString();
            }
        }

        return null;
    }

    private async Task<(string LocalId, string Email)?> LookupAccountAsync(string idToken, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{LookupUrl}?key={Uri.EscapeDataString(FirebaseOptions.WebApiKey)}";
            using var response = await httpClient.PostAsJsonAsync(url, new { idToken }, cancellationToken);
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var user = json.RootElement.GetProperty("users")[0];
            return (user.GetProperty("localId").GetString()!, user.GetProperty("email").GetString()!);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not look up account details after MFA sign-in.");
            return null;
        }
    }

    private async Task ApplySessionAsync(string idToken, string refreshToken, string localId, string email, int expiresInSeconds)
    {
        await SecureStorage.Default.SetAsync(RefreshTokenKey, refreshToken);
        await SecureStorage.Default.SetAsync(UserIdKey, localId);
        await SecureStorage.Default.SetAsync(EmailKey, email);

        _cachedIdToken = idToken;
        _idTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expiresInSeconds - 60));
        CurrentUserId = localId;
        CurrentEmail = email;
        IsSignedIn = true;
        AuthStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string GetErrorMessage(JsonDocument json)
    {
        return json.RootElement.TryGetProperty("error", out var error) &&
            error.TryGetProperty("message", out var message)
            ? message.GetString() ?? "Unknown error"
            : "Unknown error";
    }

    private static string DescribeError(string code) => code switch
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
}
