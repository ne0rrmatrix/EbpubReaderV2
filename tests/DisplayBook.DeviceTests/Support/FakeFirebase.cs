using System.Net;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// The Firebase Identity Toolkit endpoints FirebaseAuthService calls, on <see cref="FakeNetwork.Firebase"/>,
/// backed by one account whose verified/two-factor state a test can set.
/// </summary>
static class FakeFirebase
{
	public const string Email = "reader@example.com";
	public const string TotpCode = "123456";

	public static bool EmailVerified { get; set; }

	public static bool TotpEnrolled { get; set; }

	/// <summary>Firebase error codes to return from the next call to an endpoint, keyed by endpoint name.</summary>
	public static Dictionary<string, string> Failures { get; } = [];

	public static void Serve(bool emailVerified = false, bool totpEnrolled = false)
	{
		EmailVerified = emailVerified;
		TotpEnrolled = totpEnrolled;
		Failures.Clear();
		FakeServer server = FakeNetwork.Firebase;

		Route(server, "accounts:signInWithPassword", body => TotpEnrolled
			? Ok("""{ "mfaPendingCredential": "pending-1", "mfaInfo": [ { "mfaEnrollmentId": "totp-1", "totpInfo": {} } ] }""")
			: Session());
		Route(server, "accounts:signUp", _ => Session());
		Route(server, "mfaSignIn:finalize", body => body.Contains(TotpCode, StringComparison.Ordinal)
			? Ok("""{ "idToken": "id-mfa", "refreshToken": "refresh-mfa" }""")
			: Error("INVALID_TOTP_CODE"));
		Route(server, "accounts:lookup", _ => Ok($$"""
			{ "users": [ {
				"localId": "user-1", "email": "{{Email}}", "emailVerified": {{(EmailVerified ? "true" : "false")}},
				"mfaInfo": {{(TotpEnrolled ? """[ { "mfaEnrollmentId": "totp-1", "displayName": "Phone", "totpInfo": {} } ]""" : "[]")}}
			} ] }
			"""));
		Route(server, "accounts:sendOobCode", _ => Ok("{}"));
		Route(server, "mfaEnrollment:start", _ => Ok("""{ "totpSessionInfo": { "sharedSecretKey": "SECRETKEY234", "sessionInfo": "session-1" } }"""));
		Route(server, "mfaEnrollment:finalize", body =>
		{
			if (!body.Contains(TotpCode, StringComparison.Ordinal))
			{
				return Error("INVALID_TOTP_CODE");
			}

			TotpEnrolled = true;
			return Ok("""{ "idToken": "id-2", "refreshToken": "refresh-2" }""");
		});
		Route(server, "mfaEnrollment:withdraw", _ =>
		{
			TotpEnrolled = false;
			return Ok("""{ "idToken": "id-3", "refreshToken": "refresh-3" }""");
		});
	}

	static void Route(FakeServer server, string endpoint, Func<string, HttpResponseMessage> respond) =>
		server.On(
			request => request.RequestUri!.AbsoluteUri.Contains(endpoint, StringComparison.Ordinal),
			request =>
			{
				if (Failures.Remove(endpoint, out string? code))
				{
					return Error(code);
				}

				string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
				return respond(body);
			});

	static HttpResponseMessage Session() =>
		Ok($$"""{ "idToken": "id-1", "refreshToken": "refresh-1", "localId": "user-1", "email": "{{Email}}", "expiresIn": "3600" }""");

	static HttpResponseMessage Ok(string json) => FakeServer.Text(HttpStatusCode.OK, json);

	static HttpResponseMessage Error(string code) => FakeServer.Text(HttpStatusCode.BadRequest, $$"""{ "error": { "message": "{{code}}" } }""");
}
