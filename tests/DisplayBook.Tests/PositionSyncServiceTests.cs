using System.Net;
using System.Text.Json;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DisplayBook.Tests;

public class PositionSyncServiceTests
{
	const string contentHash = "abc123";
	const string documentUrl = "https://firestore.googleapis.com/v1/projects/displaybook-sync/databases/(default)/documents/users/user-1/positions/abc123";

	sealed class FakeAuthService : IFirebaseAuthService
	{
		public bool IsSignedIn { get; set; } = true;
		public string? CurrentEmail { get; set; } = "reader@example.com";
		public string? CurrentUserId { get; set; } = "user-1";
		public string? IdToken { get; set; } = "id-token";

		public event EventHandler? AuthStateChanged
		{
			add { }
			remove { }
		}

		public Task<string?> TryGetValidIdTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(IdToken);

		public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<FirebaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<FirebaseAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<FirebaseAuthResult> CompleteMfaSignInAsync(MfaChallenge challenge, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task SignOutAsync() => throw new NotSupportedException();
		public Task<AccountSecurityState?> RefreshSecurityStateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<FirebaseAuthResult> SendEmailVerificationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<TotpEnrollmentStart?> StartTotpEnrollmentAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<FirebaseAuthResult> FinalizeTotpEnrollmentAsync(string sessionInfo, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
		public Task<FirebaseAuthResult> RemoveTotpEnrollmentAsync(string enrollmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	sealed record RecordedRequest(HttpMethod Method, string? Url, string? Authorization, string? Body);

	sealed class RecordingHandler(Func<HttpResponseMessage>? respond = null) : HttpMessageHandler
	{
		public List<RecordedRequest> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add(new RecordedRequest(request.Method, request.RequestUri?.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
			return respond?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK);
		}
	}

	static HttpResponseMessage Json(HttpStatusCode status, string body) =>
		new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

	static PositionSyncService CreateService(RecordingHandler handler, FakeAuthService auth) =>
		new(new HttpClient(handler), auth, NullLogger<PositionSyncService>.Instance);

	static readonly DateTimeOffset updatedAt = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

	[Fact]
	public async Task FlushPendingPush_SendsPatchWithFirestoreFields()
	{
		RecordingHandler handler = new();
		FakeAuthService auth = new();
		using PositionSyncService service = CreateService(handler, auth);

		Task scheduled = service.SchedulePush(contentHash, "Text/ch1.xhtml", 1234, 5, 20, updatedAt);
		await service.FlushPendingPushAsync();
		await scheduled;

		RecordedRequest request = Assert.Single(handler.Requests);
		Assert.Equal(HttpMethod.Patch, request.Method);
		Assert.Equal(documentUrl, request.Url);
		Assert.Equal("Bearer id-token", request.Authorization);

		using JsonDocument body = JsonDocument.Parse(request.Body!);
		JsonElement fields = body.RootElement.GetProperty("fields");
		Assert.Equal("Text/ch1.xhtml", fields.GetProperty("resourceHref").GetProperty("stringValue").GetString());
		Assert.Equal("1234", fields.GetProperty("charOffset").GetProperty("integerValue").GetString());
		Assert.Equal("5", fields.GetProperty("page").GetProperty("integerValue").GetString());
		Assert.Equal("20", fields.GetProperty("pageCount").GetProperty("integerValue").GetString());
		Assert.Equal(updatedAt, DateTimeOffset.Parse(fields.GetProperty("updatedAt").GetProperty("timestampValue").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
	}

	[Fact]
	public async Task SchedulePush_SupersededByNewerPosition_OnlyNewestIsSent()
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		Task first = service.SchedulePush(contentHash, "Text/ch1.xhtml", 10, 1, 20, updatedAt);
		Task second = service.SchedulePush(contentHash, "Text/ch2.xhtml", 99, 2, 20, updatedAt.AddMinutes(1));
		await service.FlushPendingPushAsync();
		await Task.WhenAll(first, second);

		RecordedRequest request = Assert.Single(handler.Requests);
		Assert.Contains("Text/ch2.xhtml", request.Body);
		Assert.DoesNotContain("Text/ch1.xhtml", request.Body);
	}

	[Fact]
	public async Task FlushPendingPush_NothingScheduled_SendsNothing()
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		await service.FlushPendingPushAsync();

		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task FlushPendingPush_Twice_SendsOnlyOnce()
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		Task scheduled = service.SchedulePush(contentHash, "Text/ch1.xhtml", 0, 0, 1, updatedAt);
		await service.FlushPendingPushAsync();
		await service.FlushPendingPushAsync();
		await scheduled;

		Assert.Single(handler.Requests);
	}

	[Fact]
	public async Task SchedulePush_SignedOut_SendsNothing()
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService { IsSignedIn = false });

		await service.SchedulePush(contentHash, "Text/ch1.xhtml", 0, 0, 1, updatedAt);
		await service.FlushPendingPushAsync();

		Assert.Empty(handler.Requests);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task SchedulePush_BlankContentHash_SendsNothing(string blankHash)
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		await service.SchedulePush(blankHash, "Text/ch1.xhtml", 0, 0, 1, updatedAt);
		await service.FlushPendingPushAsync();

		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task FlushPendingPush_NoValidToken_SendsNothing()
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService { IdToken = null });

		Task scheduled = service.SchedulePush(contentHash, "Text/ch1.xhtml", 0, 0, 1, updatedAt);
		await service.FlushPendingPushAsync();
		await scheduled;

		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task FlushPendingPush_ServerErrorOrException_DoesNotThrow()
	{
		RecordingHandler failing = new(() => throw new HttpRequestException("offline"));
		using PositionSyncService service = CreateService(failing, new FakeAuthService());

		Task scheduled = service.SchedulePush(contentHash, "Text/ch1.xhtml", 0, 0, 1, updatedAt);
		await service.FlushPendingPushAsync();
		await scheduled;

		Assert.Single(failing.Requests);
	}

	[Fact]
	public async Task TryPullPosition_ParsesFirestoreDocument()
	{
		RecordingHandler handler = new(() => Json(HttpStatusCode.OK, """
			{
				"name": "projects/displaybook-sync/databases/(default)/documents/users/user-1/positions/abc123",
				"fields": {
					"resourceHref": { "stringValue": "Text/ch3.xhtml" },
					"charOffset": { "integerValue": "4321" },
					"page": { "integerValue": "7" },
					"pageCount": { "integerValue": "30" },
					"updatedAt": { "timestampValue": "2026-10-14T09:30:00Z" }
				}
			}
			"""));
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		RemoteReadingPosition? position = await service.TryPullPositionAsync(contentHash);

		Assert.Equal(new RemoteReadingPosition("Text/ch3.xhtml", 4321, 7, 30, updatedAt), position);
		RecordedRequest request = Assert.Single(handler.Requests);
		Assert.Equal(HttpMethod.Get, request.Method);
		Assert.Equal(documentUrl, request.Url);
		Assert.Equal("Bearer id-token", request.Authorization);
	}

	[Fact]
	public async Task TryPullPosition_MissingOptionalFields_UsesDefaults()
	{
		RecordingHandler handler = new(() => Json(HttpStatusCode.OK, """
			{ "fields": { "resourceHref": { "stringValue": "Text/ch1.xhtml" } } }
			"""));
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		RemoteReadingPosition? position = await service.TryPullPositionAsync(contentHash);

		Assert.Equal(new RemoteReadingPosition("Text/ch1.xhtml", -1, 0, 1, DateTimeOffset.MinValue), position);
	}

	[Theory]
	[InlineData("""{ "fields": { "page": { "integerValue": "3" } } }""")]
	[InlineData("""{ "name": "no-fields" }""")]
	[InlineData("not json")]
	public async Task TryPullPosition_UnusableDocument_ReturnsNull(string body)
	{
		RecordingHandler handler = new(() => Json(HttpStatusCode.OK, body));
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		Assert.Null(await service.TryPullPositionAsync(contentHash));
	}

	[Theory]
	[InlineData(HttpStatusCode.NotFound)]
	[InlineData(HttpStatusCode.Forbidden)]
	[InlineData(HttpStatusCode.InternalServerError)]
	public async Task TryPullPosition_NonSuccessStatus_ReturnsNull(HttpStatusCode status)
	{
		RecordingHandler handler = new(() => new HttpResponseMessage(status));
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		Assert.Null(await service.TryPullPositionAsync(contentHash));
	}

	[Fact]
	public async Task TryPullPosition_NoValidToken_ReturnsNullWithoutRequest()
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService { IdToken = null });

		Assert.Null(await service.TryPullPositionAsync(contentHash));
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task TryPullPosition_NoUserId_ReturnsNullWithoutRequest()
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService { CurrentUserId = null });

		Assert.Null(await service.TryPullPositionAsync(contentHash));
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task TryPullPosition_BlankContentHash_ReturnsNullWithoutRequest()
	{
		RecordingHandler handler = new();
		using PositionSyncService service = CreateService(handler, new FakeAuthService());

		Assert.Null(await service.TryPullPositionAsync("  "));
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task TryPullPosition_EscapesUserIdAndContentHashInUrl()
	{
		RecordingHandler handler = new(() => new HttpResponseMessage(HttpStatusCode.NotFound));
		using PositionSyncService service = CreateService(handler, new FakeAuthService { CurrentUserId = "user/1" });

		await service.TryPullPositionAsync("hash with space");

		RecordedRequest request = Assert.Single(handler.Requests);
		Assert.EndsWith("/users/user%2F1/positions/hash%20with%20space", request.Url);
	}
}
