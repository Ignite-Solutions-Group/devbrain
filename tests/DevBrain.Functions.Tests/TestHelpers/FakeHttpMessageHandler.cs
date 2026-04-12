using System.Net;

namespace DevBrain.Functions.Tests.TestHelpers;

/// <summary>
/// Minimal <see cref="HttpMessageHandler"/> stub for typed <see cref="HttpClient"/> tests. Records
/// every request (as a materialized snapshot) for post-hoc assertions and lets the test supply a
/// synchronous response callback.
///
/// <para>
/// <b>Why snapshots instead of raw <see cref="HttpRequestMessage"/>s:</b> production code commonly
/// wraps request content in a <c>using</c> block, so the <see cref="HttpContent"/> is disposed by
/// the time a test reads it. The handler reads the body into a string during <c>SendAsync</c> and
/// stores it on <see cref="RecordedRequest"/> so tests can always inspect it.
/// </para>
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    public List<RecordedRequest> ReceivedRequests { get; } = [];

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    /// <summary>Convenience: always return a JSON OK response.</summary>
    public static FakeHttpMessageHandler ReturningJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bodyText = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        ReceivedRequests.Add(new RecordedRequest(
            Method: request.Method,
            RequestUri: request.RequestUri,
            Body: bodyText));

        return _responder(request);
    }
}

public sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string? Body);
