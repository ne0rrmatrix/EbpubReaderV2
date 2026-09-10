using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.Services.Sync;

public sealed class PositionSyncService(HttpClient httpClient, IFirebaseAuthService authService, ILogger<PositionSyncService> logger) : IPositionSyncService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(4);

    private readonly Lock _pendingLock = new();
    private PendingPush? _pending;
    private CancellationTokenSource? _debounceCts;

    public void SchedulePush(string contentHash, string resourceHref, int charOffset, int page, int pageCount, DateTimeOffset updatedAtUtc)
    {
        if (!authService.IsSignedIn || string.IsNullOrWhiteSpace(contentHash))
        {
            return;
        }

        CancellationTokenSource cts;
        CancellationTokenSource? previousCts;
        lock (_pendingLock)
        {
            _pending = new PendingPush(contentHash, resourceHref, charOffset, page, pageCount, updatedAtUtc);
            previousCts = _debounceCts;
            _debounceCts = new CancellationTokenSource();
            cts = _debounceCts;
        }

        previousCts?.Cancel();
        _ = RunDebouncedPushAsync(cts.Token);
    }

    private async Task RunDebouncedPushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, cancellationToken);
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
        lock (_pendingLock)
        {
            pendingCts = _debounceCts;
            _debounceCts = null;
        }

        if (pendingCts is not null)
        {
            await pendingCts.CancelAsync();
        }

        await PushPendingAsync(CancellationToken.None);
    }

    private async Task PushPendingAsync(CancellationToken cancellationToken)
    {
        PendingPush? pending;
        lock (_pendingLock)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is null)
        {
            return;
        }

        try
        {
            var idToken = await authService.TryGetValidIdTokenAsync(cancellationToken);
            if (idToken is null || authService.CurrentUserId is not { } uid)
            {
                return;
            }

            var url = BuildDocumentUrl(uid, pending.ContentHash);
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
            request.Content = JsonContent.Create(BuildFieldsPayload(pending));

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Could not push reading position ({Status}) for content hash {ContentHash}.", response.StatusCode, pending.ContentHash);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not push reading position for content hash {ContentHash}.", pending.ContentHash);
        }
    }

    public async Task<RemoteReadingPosition?> TryPullPositionAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return null;
        }

        var idToken = await authService.TryGetValidIdTokenAsync(cancellationToken);
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
            var url = BuildDocumentUrl(uid, contentHash);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", idToken);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Could not pull reading position ({Status}) for content hash {ContentHash}.", response.StatusCode, contentHash);
                return null;
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!json.RootElement.TryGetProperty("fields", out var fields))
            {
                return null;
            }

            var resourceHref = GetString(fields, "resourceHref") ?? string.Empty;
            var charOffset = GetInt(fields, "charOffset") ?? -1;
            var page = GetInt(fields, "page") ?? 0;
            var pageCount = GetInt(fields, "pageCount") ?? 1;
            var updatedAt = GetTimestamp(fields, "updatedAt") ?? DateTimeOffset.MinValue;

            if (string.IsNullOrWhiteSpace(resourceHref))
            {
                return null;
            }

            return new RemoteReadingPosition(resourceHref, charOffset, page, pageCount, updatedAt);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not pull reading position for content hash {ContentHash}.", contentHash);
            return null;
        }
    }

    private static string BuildDocumentUrl(string uid, string contentHash) =>
        $"https://firestore.googleapis.com/v1/projects/{FirebaseOptions.ProjectId}/databases/(default)/documents/users/{Uri.EscapeDataString(uid)}/positions/{Uri.EscapeDataString(contentHash)}";

    private static object BuildFieldsPayload(PendingPush pending) => new
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

    private static string? GetString(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var field) && field.TryGetProperty("stringValue", out var value)
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var field) && field.TryGetProperty("integerValue", out var value) &&
            int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? GetTimestamp(JsonElement fields, string name) =>
        fields.TryGetProperty(name, out var field) && field.TryGetProperty("timestampValue", out var value) &&
            DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private sealed record PendingPush(string ContentHash, string ResourceHref, int CharOffset, int Page, int PageCount, DateTimeOffset UpdatedAtUtc);
}
