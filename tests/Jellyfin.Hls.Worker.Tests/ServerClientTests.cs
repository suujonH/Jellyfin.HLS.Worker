using System.Net;
using System.Security.Cryptography;
using Jellyfin.Hls.Worker.Protocol;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class ServerClientTests
{
    private static readonly Uri _server = new("http://jellyfin:8096/jf");

    [Fact]
    public async Task Requests_CarryTheKeyAndTheProtocol_BelowTheBasePath()
    {
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Json(new ProtocolVersion(1))));
        using var client = new ServerClient(_server, "secret", TimeSpan.FromSeconds(5), server);

        await client.CheckProtocolAsync(CancellationToken.None);

        var (request, _) = Assert.Single(server.Requests);
        Assert.Equal("/jf/HlsWorker/Protocol", request.RequestUri!.AbsolutePath);
        Assert.Contains("Token=\"secret\"", request.Headers.Authorization!.Parameter, StringComparison.Ordinal);
        Assert.Equal("1", Assert.Single(request.Headers.GetValues(ServerClient.ProtocolHeader)));
    }

    [Fact]
    public async Task CheckProtocol_RefusesAnotherVersion()
    {
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Json(new ProtocolVersion(2))));
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);

        await Assert.ThrowsAsync<ServerException>(() => client.CheckProtocolAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Poll_WithoutJob_IsNull()
    {
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Status(HttpStatusCode.NoContent)));
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);

        Assert.Null(await client.PollAsync("w", CancellationToken.None));
        Assert.Contains("\"Worker\":\"w\"", server.Requests.Single().Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, true, false)]
    [InlineData(HttpStatusCode.BadRequest, false, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, true)]
    [InlineData(HttpStatusCode.TooManyRequests, false, true)]
    public async Task Errors_KeepTheStatusAndTheMessage(HttpStatusCode status, bool conflict, bool transient)
    {
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Status(status, "the reason")));
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);

        var error = await Assert.ThrowsAsync<ServerException>(() => client.HeartbeatAsync(Guid.NewGuid(), "t", CancellationToken.None));

        Assert.Equal(conflict, error.IsConflict);
        Assert.Equal(transient, ServerException.IsTransient(error));
        Assert.Contains("the reason", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeout_IsTransient()
    {
        using var handler = new SlowHandler();
        using var client = new ServerClient(_server, "k", TimeSpan.FromMilliseconds(100), handler);

        var error = await Assert.ThrowsAsync<ServerException>(() => client.HeartbeatAsync(Guid.NewGuid(), "t", CancellationToken.None));

        Assert.True(ServerException.IsTransient(error));
    }

    [Fact]
    public async Task Download_FollowsARedirectWithoutTheKey_AndHashes()
    {
        var content = RandomNumberGenerator.GetBytes(300_000);
        var server = new FakeServer((request, _) => Task.FromResult(request.RequestUri!.Host == "jellyfin"
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://storage/bucket/file?sig=1") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }));
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);
        var path = Path.GetTempFileName();
        try
        {
            var sha = await client.DownloadAsync("HlsWorker/Jobs/x/Input?leaseToken=t", path, CancellationToken.None);

            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), sha);
            Assert.Equal(content, await File.ReadAllBytesAsync(path));
            var requests = server.Requests.Select(r => r.Request).ToList();
            Assert.Equal("/jf/HlsWorker/Jobs/x/Input", requests[0].RequestUri!.AbsolutePath);
            Assert.NotNull(requests[0].Headers.Authorization);
            Assert.Null(requests[1].Headers.Authorization);
            Assert.False(requests[1].Headers.Contains(ServerClient.ProtocolHeader));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // A server that does not answer; like a socket, it gives up when the request is cancelled.
    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return FakeServer.Status(HttpStatusCode.OK);
        }
    }
}
