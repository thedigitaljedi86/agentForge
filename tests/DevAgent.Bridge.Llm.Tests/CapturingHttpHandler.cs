namespace DevAgent.Bridge.Llm.Tests;

using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

/// <summary>
/// A test <see cref="HttpMessageHandler"/> that records the outgoing request and
/// returns a queued JSON response. Lets us exercise the LLM clients' wire format
/// (headers, body, response mapping) without any real network call.
/// </summary>
public sealed class CapturingHttpHandler : HttpMessageHandler
{
    private readonly Queue<string> _responses;

    public CapturingHttpHandler(params string[] responses)
    {
        _responses = new Queue<string>(responses);
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }
    public JsonObject? LastJson { get; private set; }
    public Uri? LastUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastUri = request.RequestUri;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        LastJson = LastBody is null ? null : JsonNode.Parse(LastBody) as JsonObject;

        var payload = _responses.Count > 0 ? _responses.Dequeue() : "{}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload),
        };
    }

    public HttpClient Client() => new(this);

    public string? Header(string name) =>
        LastRequest is not null && LastRequest.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : null;
}
