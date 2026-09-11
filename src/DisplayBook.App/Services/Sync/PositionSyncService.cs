using System.Net.Http.Json;
using System.Text.Json;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Picker;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services.Sync;

public sealed partial class PositionSyncService(HttpClient httpClient, IFirebaseAuthService authService, ILogger<PositionSyncService> logger) : IPositionSyncService, IDisposable
{
	static readonly TimeSpan debounceDelay = TimeSpan.FromSeconds(4);

	readonly Lock pendingLock = new();
	PendingPush? pending;
	CancellationTokenSource? debounceCts;
	bool disposedValue;

	public void SchedulePush(string contentHash, string resourceHref, int charOffset, int page, int pageCount, DateTimeOffset updatedAtUtc)
	{
		if (!authService.IsSignedIn || string.IsNullOrWhiteSpace(contentHash))
		{
			return;
		}

		CancellationTokenSource cts;
		CancellationTokenSource? previousCts;
		lock (pendingLock)
		{
			pending = new PendingPush(contentHash, resourceHref, charOffset, page, pageCount, updatedAtUtc);
			previousCts = debounceCts;
			debounceCts = new CancellationTokenSource();
			cts = debounceCts;
		}

		previousCts?.Cancel();
		_ = RunDebouncedPushAsync(cts.Token);
	}

	async Task RunDebouncedPushAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(debounceDelay, cancellationToken);
		}
		catch (TaskCanceledException)
		{
			return;
		}

		await PushPendingAsync(cancellationToken);
	}

	public async Task FlushPendingPushAsync()
	{
		CancellationTokenSource? pendingCts;
		lock (pendingLock)
		{
			pendingCts = debounceCts;
			debounceCts = null;
		}

		if (pendingCts is not null)
		{
			await pendingCts.CancelAsync();
		}

		await PushPendingAsync(CancellationToken.None);
	}

	async Task PushPendingAsync(CancellationToken cancellationToken)
	{
		PendingPush? pending1;
		lock (pendingLock)
		{
			pending1 = this.pending;
			this.pending = null;
		}

		if (pending1 is null)
		{
			return;
		}

		try
		{
			string? idToken = await authService.TryGetValidIdTokenAsync(cancellationToken);
			if (idToken is null || authService.CurrentUserId is not { } uid)
			{
				return;
			}

			string url = BuildDocumentUrl(uid, pending1.ContentHash);
			using HttpRequestMessage request = new(HttpMethod.Patch, url);
			request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
			request.Content = JsonContent.Create(BuildFieldsPayload(pending1));

			using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				logger.LogWarning("Could not push reading position ({Status}) for content hash {ContentHash}.", response.StatusCode, pending1.ContentHash);
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			logger.LogWarning(exception, "Could not push reading position for content hash {ContentHash}.", pending1.ContentHash);
		}
	}

	public async Task<RemoteReadingPosition?> TryPullPositionAsync(string contentHash, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(contentHash))
		{
			return null;
		}

		string? idToken = await authService.TryGetValidIdTokenAsync(cancellationToken);
		if (idToken is null)
		{
			return null;
		}

		if (authService.CurrentUserId is not { } uid)
		{
			return null;
		}

		try
		{
			string url = BuildDocumentUrl(uid, contentHash);
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
			using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
			if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
			{
				return null;
			}

			if (!response.IsSuccessStatusCode)
			{
				logger.LogWarning("Could not pull reading position ({Status}) for content hash {ContentHash}.", response.StatusCode, contentHash);
				return null;
			}

			using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
			if (!json.RootElement.TryGetProperty("fields", out JsonElement fields))
			{
				return null;
			}

			string resourceHref = GetString(fields, "resourceHref") ?? string.Empty;
			int charOffset = GetInt(fields, "charOffset") ?? -1;
			int page = GetInt(fields, "page") ?? 0;
			int pageCount = GetInt(fields, "pageCount") ?? 1;
			DateTimeOffset updatedAt = GetTimestamp(fields, "updatedAt") ?? DateTimeOffset.MinValue;

			return string.IsNullOrWhiteSpace(resourceHref)
				? null
				: new RemoteReadingPosition(resourceHref, charOffset, page, pageCount, updatedAt);
		}
		catch (Exception exception)
		{
			logger.LogWarning(exception, "Could not pull reading position for content hash {ContentHash}.", contentHash);
			return null;
		}
	}

	static string BuildDocumentUrl(string uid, string contentHash) =>
		$"https://firestore.googleapis.com/v1/projects/{FirebaseOptions.ProjectId}/databases/(default)/documents/users/{Uri.EscapeDataString(uid)}/positions/{Uri.EscapeDataString(contentHash)}";

	static object BuildFieldsPayload(PendingPush pending) => new
	{
		fields = new
		{
			resourceHref = new { stringValue = pending.ResourceHref },
			charOffset = new { integerValue = pending.CharOffset.ToString() },
			page = new { integerValue = pending.Page.ToString() },
			pageCount = new { integerValue = pending.PageCount.ToString() },
			updatedAt = new { timestampValue = pending.UpdatedAtUtc.UtcDateTime.ToString("O") }
		}
	};

	static string? GetString(JsonElement fields, string name) =>
		fields.TryGetProperty(name, out JsonElement field) && field.TryGetProperty("stringValue", out JsonElement value)
			? value.GetString()
			: null;

	static int? GetInt(JsonElement fields, string name) =>
		fields.TryGetProperty(name, out JsonElement field) && field.TryGetProperty("integerValue", out JsonElement value) &&
			int.TryParse(value.GetString(), out int parsed)
			? parsed
			: null;

	static DateTimeOffset? GetTimestamp(JsonElement fields, string name) =>
		fields.TryGetProperty(name, out JsonElement field) && field.TryGetProperty("timestampValue", out JsonElement value) &&
			DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
			? parsed
			: null;

	sealed record PendingPush(string ContentHash, string ResourceHref, int CharOffset, int Page, int PageCount, DateTimeOffset UpdatedAtUtc);

	void Dispose(bool disposing)
	{
		if (!disposedValue)
		{
			if (disposing)
			{
				debounceCts?.Dispose();
				debounceCts = null;
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
