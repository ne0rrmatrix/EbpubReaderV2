using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplayBook.App.Services.Sync;

sealed record IdentityToolkitSignRequest(string Email, string Password, bool ReturnSecureToken = true);

/// <summary>Body of the Identity Toolkit calls that only need to identify the caller (accounts:lookup).</summary>
sealed record IdTokenRequest(string IdToken);

/// <summary>Body of accounts:sendOobCode (<c>requestType</c> is e.g. <c>VERIFY_EMAIL</c>).</summary>
sealed record SendOobCodeRequest(string RequestType, string IdToken);

/// <summary>
/// Marker object the mfaEnrollment:start body requires to select the TOTP factor.
/// It carries no state and serializes to <c>{}</c>.
/// </summary>
sealed class TotpEnrollmentInfo
{
    /// <summary>Value serialized by <see cref="JsonSerializer"/> for this stateless marker.</summary>
    public static TotpEnrollmentInfo Empty { get; } = new();

    public TotpEnrollmentInfo()
    {
    }
}

sealed record TotpEnrollmentStartRequest(string IdToken, TotpEnrollmentInfo TotpEnrollmentInfo);

sealed record TotpVerificationSession(string SessionInfo, string VerificationCode);

sealed record TotpEnrollmentFinalizeRequest(string IdToken, string? DisplayName, TotpVerificationSession TotpVerificationInfo);

sealed record TotpEnrollmentWithdrawRequest(string IdToken, string MfaEnrollmentId);

sealed record TotpVerificationCode(string VerificationCode);

sealed record MfaSignInFinalizeRequest(string MfaPendingCredential, string MfaEnrollmentId, TotpVerificationCode TotpVerificationInfo);

/// <summary>
/// Source-generated metadata for every request body <see cref="FirebaseAuthService"/> POSTs.
/// Release builds trim/AOT the app with reflection-based serialization disabled, so these bodies
/// have to be declared types with generated metadata rather than anonymous objects.
/// Responses are read with <see cref="JsonDocument"/> and need nothing here.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IdentityToolkitSignRequest))]
[JsonSerializable(typeof(IdTokenRequest))]
[JsonSerializable(typeof(SendOobCodeRequest))]
[JsonSerializable(typeof(TotpEnrollmentStartRequest))]
[JsonSerializable(typeof(TotpEnrollmentFinalizeRequest))]
[JsonSerializable(typeof(TotpEnrollmentWithdrawRequest))]
[JsonSerializable(typeof(MfaSignInFinalizeRequest))]
partial class AuthJsonContext : JsonSerializerContext
{
}