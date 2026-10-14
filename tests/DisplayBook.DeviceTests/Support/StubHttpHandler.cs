using System.Net;

namespace DisplayBook.DeviceTests.Support;

sealed record RecordedRequest(HttpMethod Method, string Url, string? Body);

/// <summary>
/// Answers each request from a queue of canned responses (500 once the queue is empty) and
/// records what was sent, so services that talk to a REST API can run without a network.
/// </summary>
sealed partial class StubHttpHandler : HttpMessageHandler
{
	readonly Queue<Func<HttpResponseMessage>> responses = new();

	public List<RecordedRequest> Requests { get; } = [];

	public StubHttpHandler Respond(HttpStatusCode status, string json)
	{
		responses.Enqueue(() => new HttpResponseMessage(status)
		{
			Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
		});
		return this;
	}

	public StubHttpHandler Throw(Exception exception)
	{
		responses.Enqueue(() => throw exception);
		return this;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
		Requests.Add(new RecordedRequest(request.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, body));
		return responses.Count > 0
			? responses.Dequeue()()
			: new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
	}
}
