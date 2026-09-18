using System.Net;

namespace DaprMQ.Client.Tests;

/// <summary>
/// Minimal fake HttpMessageHandler for exercising DaprMQClient's REST calls without a live
/// server. No such fake exists elsewhere in this repo - this is the first one.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    public FakeHttpMessageHandler(HttpStatusCode statusCode, string? jsonBody = null)
        : this(_ => Task.FromResult(BuildResponse(statusCode, jsonBody)))
    {
    }

    private static HttpResponseMessage BuildResponse(HttpStatusCode statusCode, string? jsonBody)
    {
        var response = new HttpResponseMessage(statusCode);
        if (jsonBody != null)
        {
            response.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        }
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
        return await _responder(request);
    }
}
