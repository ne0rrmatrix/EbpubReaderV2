using System.Net;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Sync;
using DisplayBook.DeviceTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace DisplayBook.DeviceTests.Tests;

/// <summary>
/// The parts of <see cref="FirebaseAuthService"/> that depend on <see cref="SecureStorage"/>:
/// a session has to survive an app restart, and signing out or a rejected refresh has to clear it.
/// HTTP is stubbed; the secure storage is the platform's real one, scoped to this test app.
/// </summary>
public class FirebaseAuthServiceTests : AppDataTest
{
	// The secure-storage key the refresh token is persisted under. Renaming it signs every
	// existing user out, so it is pinned here on purpose.
	const string refreshTokenKey = "displaybook.sync.refreshToken";

	const string signInResponse = """
		{ "idToken": "id-1", "refreshToken": "refresh-1", "localId": "user-1", "expiresIn": "3600" }
		""";

	static FirebaseAuthService CreateService(StubHttpHandler handler) =>
		new(new HttpClient(handler), NullLogger<FirebaseAuthService>.Instance);

	static async Task<(FirebaseAuthService Service, StubHttpHandler Handler)> SignedInAsync()
	{
		StubHttpHandler handler = new StubHttpHandler().Respond(HttpStatusCode.OK, signInResponse);
		FirebaseAuthService service = CreateService(handler);
		await service.InitializeAsync();
		FirebaseAuthResult result = await service.SignInAsync("reader@example.com", "secret");
		Assert.True(result.Succeeded, result.ErrorMessage);
		return (service, handler);
	}

	[Fact]
	public async Task SignIn_Success_SignsInAndRaisesAuthStateChanged()
	{
		StubHttpHandler handler = new StubHttpHandler().Respond(HttpStatusCode.OK, signInResponse);
		using FirebaseAuthService service = CreateService(handler);
		await service.InitializeAsync();
		int changes = 0;
		service.AuthStateChanged += (_, _) => changes++;

		FirebaseAuthResult result = await service.SignInAsync("reader@example.com", "secret");

		Assert.Equal(FirebaseAuthResult.Success, result);
		Assert.Equal((true, "reader@example.com", "user-1"), (service.IsSignedIn, service.CurrentEmail, service.CurrentUserId));
		Assert.Equal(1, changes);
		RecordedRequest request = Assert.Single(handler.Requests);
		Assert.StartsWith("https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword", request.Url);
		Assert.Contains("reader@example.com", request.Body);
	}

	[Fact]
	public async Task SignIn_PersistsTheSessionAcrossARestart()
	{
		(FirebaseAuthService first, _) = await SignedInAsync();
		first.Dispose();

		using FirebaseAuthService restarted = CreateService(new StubHttpHandler());
		await restarted.InitializeAsync();

		Assert.Equal((true, "reader@example.com", "user-1"), (restarted.IsSignedIn, restarted.CurrentEmail, restarted.CurrentUserId));
		Assert.Equal("refresh-1", await SecureStorage.Default.GetAsync(refreshTokenKey));
	}

	[Fact]
	public async Task TryGetValidIdToken_RightAfterSignIn_UsesTheCachedToken()
	{
		(FirebaseAuthService service, StubHttpHandler handler) = await SignedInAsync();
		using (service)
		{
			Assert.Equal("id-1", await service.TryGetValidIdTokenAsync());
			Assert.Single(handler.Requests);
		}
	}

	[Fact]
	public async Task TryGetValidIdToken_AfterRestart_RefreshesWithTheStoredToken()
	{
		(FirebaseAuthService first, _) = await SignedInAsync();
		first.Dispose();
		StubHttpHandler handler = new StubHttpHandler().Respond(HttpStatusCode.OK, """
			{ "id_token": "id-2", "refresh_token": "refresh-2", "expires_in": "3600" }
			""");
		using FirebaseAuthService restarted = CreateService(handler);
		await restarted.InitializeAsync();

		string? token = await restarted.TryGetValidIdTokenAsync();

		Assert.Equal("id-2", token);
		RecordedRequest request = Assert.Single(handler.Requests);
		Assert.StartsWith("https://securetoken.googleapis.com/v1/token", request.Url);
		Assert.Contains("grant_type=refresh_token", request.Body);
		Assert.Contains("refresh_token=refresh-1", request.Body);
		Assert.Equal("refresh-2", await SecureStorage.Default.GetAsync(refreshTokenKey));
	}

	[Fact]
	public async Task TryGetValidIdToken_RefreshRejected_SignsOut()
	{
		(FirebaseAuthService first, _) = await SignedInAsync();
		first.Dispose();
		StubHttpHandler handler = new StubHttpHandler().Respond(HttpStatusCode.BadRequest, """
			{ "error": { "message": "TOKEN_EXPIRED" } }
			""");
		using FirebaseAuthService restarted = CreateService(handler);
		await restarted.InitializeAsync();

		Assert.Null(await restarted.TryGetValidIdTokenAsync());
		Assert.False(restarted.IsSignedIn);
		Assert.Null(await SecureStorage.Default.GetAsync(refreshTokenKey));
	}

	[Fact]
	public async Task TryGetValidIdToken_Offline_StaysSignedIn()
	{
		(FirebaseAuthService first, _) = await SignedInAsync();
		first.Dispose();
		StubHttpHandler handler = new StubHttpHandler().Throw(new HttpRequestException("offline"));
		using FirebaseAuthService restarted = CreateService(handler);
		await restarted.InitializeAsync();

		Assert.Null(await restarted.TryGetValidIdTokenAsync());
		Assert.True(restarted.IsSignedIn);
		Assert.Equal("refresh-1", await SecureStorage.Default.GetAsync(refreshTokenKey));
	}

	[Fact]
	public async Task TryGetValidIdToken_SignedOut_ReturnsNullWithoutARequest()
	{
		StubHttpHandler handler = new();
		using FirebaseAuthService service = CreateService(handler);
		await service.InitializeAsync();

		Assert.Null(await service.TryGetValidIdTokenAsync());
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task SignOut_ClearsTheStoredSession()
	{
		(FirebaseAuthService service, _) = await SignedInAsync();
		int changes = 0;
		service.AuthStateChanged += (_, _) => changes++;

		await service.SignOutAsync();
		service.Dispose();

		Assert.Equal(1, changes);
		Assert.Null(await SecureStorage.Default.GetAsync(refreshTokenKey));
		using FirebaseAuthService restarted = CreateService(new StubHttpHandler());
		await restarted.InitializeAsync();
		Assert.False(restarted.IsSignedIn);
	}

	[Fact]
	public async Task SignIn_WrongPassword_ReturnsAReadableErrorAndStoresNothing()
	{
		StubHttpHandler handler = new StubHttpHandler().Respond(HttpStatusCode.BadRequest, """
			{ "error": { "message": "INVALID_LOGIN_CREDENTIALS" } }
			""");
		using FirebaseAuthService service = CreateService(handler);
		await service.InitializeAsync();

		FirebaseAuthResult result = await service.SignInAsync("reader@example.com", "wrong");

		Assert.Equal(FirebaseAuthResult.Failure("Incorrect email or password."), result);
		Assert.False(service.IsSignedIn);
		Assert.Null(await SecureStorage.Default.GetAsync(refreshTokenKey));
	}

	[Fact]
	public async Task SignIn_TotpEnrolled_ReturnsAChallengeInsteadOfSigningIn()
	{
		StubHttpHandler handler = new StubHttpHandler().Respond(HttpStatusCode.OK, """
			{
				"mfaPendingCredential": "pending-1",
				"mfaInfo": [ { "mfaEnrollmentId": "enrollment-1", "totpInfo": {} } ]
			}
			""");
		using FirebaseAuthService service = CreateService(handler);
		await service.InitializeAsync();

		FirebaseAuthResult result = await service.SignInAsync("reader@example.com", "secret");

		Assert.Equal(FirebaseAuthResult.NeedsMfa(new MfaChallenge("pending-1", "enrollment-1")), result);
		Assert.False(service.IsSignedIn);
		Assert.Null(await SecureStorage.Default.GetAsync(refreshTokenKey));
	}
}
