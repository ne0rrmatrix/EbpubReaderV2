using System.Net;
using System.Text;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// One in-memory server per named <see cref="HttpClient"/> the app registers. <see cref="MauiProgram"/>
/// points each named client's primary handler here, so services resolved from DI (the OPDS parser
/// and download queue, the metadata clients, Firebase auth and sync) never reach the network.
/// Unmatched requests get a 404.
/// </summary>
static class FakeNetwork
{
	public static FakeServer Opds { get; } = new();
	public static FakeServer Firebase { get; } = new();
	public static FakeServer GoogleBooks { get; } = new();
	public static FakeServer OpenLibrary { get; } = new();

	/// <summary>The unnamed default client (for example, cover downloads).</summary>
	public static FakeServer Default { get; } = new();

	public static void Reset()
	{
		Opds.Reset();
		Firebase.Reset();
		GoogleBooks.Reset();
		OpenLibrary.Reset();
		Default.Reset();
	}
}

sealed class FakeServer
{
	readonly Lock gate = new();
	readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond)> routes = [];
	readonly List<RecordedRequest> requests = [];

	public IReadOnlyList<RecordedRequest> Requests
	{
		get
		{
			lock (gate)
			{
				return [.. requests];
			}
		}
	}

	public void Reset()
	{
		lock (gate)
		{
			routes.Clear();
			requests.Clear();
		}
	}

	/// <summary>Adds a route. Later routes win over earlier ones that match the same request.</summary>
	public FakeServer On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
	{
		lock (gate)
		{
			routes.Insert(0, (match, respond));
		}

		return this;
	}

	public FakeServer On(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
		On(match, request => Task.FromResult(respond(request)));

	/// <summary>Matches on the full URL, query included.</summary>
	public FakeServer OnUrl(string url, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
		On(request => string.Equals(request.RequestUri?.AbsoluteUri, new Uri(url).AbsoluteUri, StringComparison.Ordinal), respond);

	/// <summary>Matches on scheme, host and path, ignoring the query.</summary>
	public FakeServer OnPath(string urlWithoutQuery, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
		On(request => string.Equals(request.RequestUri?.GetLeftPart(UriPartial.Path), new Uri(urlWithoutQuery).GetLeftPart(UriPartial.Path), StringComparison.Ordinal), respond);

	public FakeServer OnUrl(string url, string body, string mediaType = "application/atom+xml") =>
		OnUrl(url, _ => Text(HttpStatusCode.OK, body, mediaType));

	public static HttpResponseMessage Text(HttpStatusCode status, string body, string mediaType = "application/json") =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

	public static HttpResponseMessage Bytes(byte[] body, string mediaType = "application/epub+zip") =>
		new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { { "Content-Type", mediaType } } } };

	internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		Func<HttpRequestMessage, Task<HttpResponseMessage>>? respond;
		lock (gate)
		{
			requests.Add(new RecordedRequest(request.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, body));
			respond = routes.FirstOrDefault(route => route.Match(request)).Respond;
		}

		if (respond is null)
		{
			return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
		}

		HttpResponseMessage response = await respond(request);
		response.RequestMessage ??= request;
		return response;
	}
}

/// <summary>The primary handler of a named client: forwards to that client's <see cref="FakeServer"/>.</summary>
sealed partial class FakeServerHandler(FakeServer server) : HttpMessageHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
		server.SendAsync(request, cancellationToken);
}
