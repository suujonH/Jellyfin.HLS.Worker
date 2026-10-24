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
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Json(new ProtocolVersion(3))));
        using var client = new ServerClient(_server, "secret", TimeSpan.FromSeconds(5), server);

        await client.CheckProtocolAsync(CancellationToken.None);

        var (request, _) = Assert.Single(server.Requests);
        Assert.Equal("/jf/HlsWorker/Protocol", request.RequestUri!.AbsolutePath);
        Assert.Contains("Token=\"secret\"", request.Headers.Authorization!.Parameter, StringComparison.Ordinal);
        Assert.Equal("3", Assert.Single(request.Headers.GetValues(ServerClient.ProtocolHeader)));
    }

    // As the server wrote it: GUIDs as 32 digits, without hyphens.
    [Fact]
    public async Task GetLibraries_ReadsTheGuidsOfTheServer()
    {
        var server = new FakeServer((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """[{"Id":"3227ce1e069754c594af25ea66d69fc7","Name":"电影","Qualities":[1080,720,480]}]""",
                System.Text.Encoding.UTF8,
                "application/json")
        }));
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);

        var library = Assert.Single(await client.GetLibrariesAsync(CancellationToken.None));

        Assert.Equal(Guid.Parse("3227ce1e-0697-54c5-94af-25ea66d69fc7"), library.Id);
        Assert.Equal("电影", library.Name);
        Assert.Equal("\"3227ce1e069754c594af25ea66d69fc7\"", System.Text.Json.JsonSerializer.Serialize(library.Id, ServerClient.Json));
    }

    [Fact]
    public async Task CheckProtocol_RefusesAnotherVersion()
    {
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Json(new ProtocolVersion(1))));
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);

        await Assert.ThrowsAsync<ServerException>(() => client.CheckProtocolAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Poll_WithoutJob_IsNull()
    {
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Status(HttpStatusCode.NoContent)));
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);

        var library = Guid.Parse("3227ce1e-0697-54c5-94af-25ea66d69fc7");

        Assert.Null(await client.PollAsync(new WorkerRequest("w", library, [library], [], [new QualityFilter(library, [720])]), CancellationToken.None));
        Assert.Equal(
            """{"Worker":"w","WorkerId":"3227ce1e069754c594af25ea66d69fc7","Libraries":["3227ce1e069754c594af25ea66d69fc7"],"ExcludedItems":[],"ExcludedQualities":[{"LibraryId":"3227ce1e069754c594af25ea66d69fc7","Excluded":[720]}]}""",
            server.Requests.Single().Body);
    }

    [Fact]
    public async Task RegisterWorker_SendsTheIdItHas_AndReadsTheOneGiven()
    {
        var id = Guid.NewGuid();
        var server = new FakeServer((_, _) => Task.FromResult(FakeServer.Json(new WorkerRegistered(id))));
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);

        Assert.Equal(id, await client.RegisterWorkerAsync(null, "w", CancellationToken.None));

        var (request, body) = Assert.Single(server.Requests);
        Assert.Equal((HttpMethod.Post, "/jf/HlsWorker/Workers"), (request.Method, request.RequestUri!.AbsolutePath));
        Assert.Equal("""{"Id":null,"Name":"w"}""", body);
    }

    [Fact]
    public async Task Upload_StartsWritesChunksAndCompletes_AtTheServer()
    {
        var library = Guid.NewGuid();
        var worker = Guid.NewGuid();
        byte[]? chunk = null;
        var server = new FakeServer(async (request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
                chunk = await request.Content.ReadAsByteArrayAsync();
            }

            return request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/Uploads", StringComparison.Ordinal)
                ? FakeServer.Json(new LibraryUpload("u1"))
                : FakeServer.Status(HttpStatusCode.NoContent);
        });
        using var client = new ServerClient(_server, "k", TimeSpan.FromSeconds(5), server);

        Assert.Equal("u1", await client.StartUploadAsync(library, new LibraryUploadStart(null, "a/b.strm", 3, worker), CancellationToken.None));
        await client.PutChunkAsync("u1", 16, new byte[] { 1, 2, 3 }, CancellationToken.None);
        await client.CompleteUploadAsync("u1", "s3://b/a.mkv", CancellationToken.None);
        await client.AbortUploadAsync("u1", CancellationToken.None);
        await client.ReturnAsync(library, "t", CancellationToken.None);

        var requests = server.Requests.ToList();
        Assert.Equal(
            [
                (HttpMethod.Post, $"/jf/HlsWorker/Libraries/{library:N}/Uploads"),
                (HttpMethod.Put, "/jf/HlsWorker/Uploads/u1"),
                (HttpMethod.Post, "/jf/HlsWorker/Uploads/u1/Complete"),
                (HttpMethod.Delete, "/jf/HlsWorker/Uploads/u1"),
                (HttpMethod.Post, $"/jf/HlsWorker/Jobs/{library:N}/Return")
            ],
            requests.Select(r => (r.Request.Method, r.Request.RequestUri!.AbsolutePath)));
        Assert.Contains("\"Path\":\"a/b.strm\"", requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("?offset=16", requests[1].Request.RequestUri!.Query);
        Assert.Equal(new byte[] { 1, 2, 3 }, chunk);
        Assert.Equal("""{"ShortcutTarget":"s3://b/a.mkv","OperationId":null}""", requests[2].Body);
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
