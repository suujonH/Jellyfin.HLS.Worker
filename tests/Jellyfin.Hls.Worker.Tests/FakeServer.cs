using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Jellyfin.Hls.Worker.Protocol;

namespace Jellyfin.Hls.Worker.Tests;

/// <summary>
/// Serves the requests of a <see cref="ServerClient"/> from a route table, and records them.
/// </summary>
internal sealed class FakeServer : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, Task<HttpResponseMessage>> _route;

    public FakeServer(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> route)
    {
        _route = route;
    }

    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = JsonContent.Create(value, options: ServerClient.Json) };

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "")
        => new(status) { Content = new StringContent(body) };

    public IEnumerable<string> Paths => Requests.Select(r => r.Request.RequestUri!.AbsolutePath);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request, body));
        return await _route(request, body);
    }
}
