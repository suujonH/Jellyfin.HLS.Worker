using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

/// <summary>
/// Serves the uploads into the folders of the libraries as the server does, keeping what each one completed, and the working marks
/// of the files being added.
/// </summary>
internal sealed class FakeLibrary
{
    private readonly ConcurrentDictionary<string, (Guid LibraryId, LibraryUploadStart Start, MemoryStream Data)> _uploads = new();

    /// <summary>Gets the uploads completed, in order.</summary>
    public ConcurrentQueue<LibraryFile> Files { get; } = new();

    /// <summary>Gets the operations that hold the files, by <c>location|path</c>.</summary>
    public ConcurrentDictionary<string, string> Marks { get; } = new();

    /// <summary>Gets the operations that held a file before each claim, in order.</summary>
    public ConcurrentQueue<string?> Claimed { get; } = new();

    /// <summary>Gets or sets what answers the completion of an upload before the rest: its path, or null to go on.</summary>
    public Func<string, HttpResponseMessage?>? Refuse { get; set; }

    /// <summary>
    /// Marks a file as held by another operation, as another worker would.
    /// </summary>
    /// <param name="location">The folder of the library.</param>
    /// <param name="path">The file.</param>
    /// <param name="operationId">The operation.</param>
    public void Take(string? location, string path, string operationId) => Marks[location + "|" + path] = operationId;

    /// <summary>
    /// Serves a request of the uploads.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="body">Its body as text.</param>
    /// <returns>The response; <c>null</c> when it is not a request of the uploads.</returns>
    public async Task<HttpResponseMessage?> TryServeAsync(HttpRequestMessage request, string body)
    {
        var path = request.RequestUri!.AbsolutePath;
        var parts = path.Split('/');
        if (request.Method == HttpMethod.Put && path.EndsWith("/Working", StringComparison.Ordinal))
        {
            var claim = JsonSerializer.Deserialize<WorkingClaim>(body, ServerClient.Json)!;
            var key = claim.Location + "|" + claim.Path;
            var previous = Marks.TryGetValue(key, out var held) ? held : null;
            Marks[key] = claim.OperationId;
            Claimed.Enqueue(previous);
            return FakeServer.Json(new WorkingState(previous));
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/Working/Read", StringComparison.Ordinal))
        {
            var read = JsonSerializer.Deserialize<WorkingPath>(body, ServerClient.Json)!;
            return FakeServer.Json(new WorkingState(Marks.TryGetValue(read.Location + "|" + read.Path, out var held) ? held : null));
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/Working/Release", StringComparison.Ordinal))
        {
            var release = JsonSerializer.Deserialize<WorkingRelease>(body, ServerClient.Json)!;
            var key = release.Location + "|" + release.Path;
            if (Marks.TryGetValue(key, out var held) && held == release.OperationId)
            {
                Marks.TryRemove(key, out _);
            }

            return FakeServer.Status(HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/Uploads", StringComparison.Ordinal))
        {
            var id = Guid.NewGuid().ToString("N");
            _uploads[id] = (Guid.Parse(parts[^2]), JsonSerializer.Deserialize<LibraryUploadStart>(body, ServerClient.Json)!, new MemoryStream());
            return FakeServer.Json(new LibraryUpload(id));
        }

        if (!path.Contains("/HlsWorker/Uploads/", StringComparison.Ordinal))
        {
            return null;
        }

        var uploadId = parts[path.EndsWith("/Complete", StringComparison.Ordinal) ? ^2 : ^1];
        if (!_uploads.TryGetValue(uploadId, out var upload))
        {
            return FakeServer.Status(HttpStatusCode.NotFound);
        }

        if (request.Method == HttpMethod.Put)
        {
            var offset = long.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["offset"]!, System.Globalization.CultureInfo.InvariantCulture);
            var chunk = await request.Content!.ReadAsByteArrayAsync();
            upload.Data.SetLength(offset);
            upload.Data.Position = offset;
            upload.Data.Write(chunk);
            return FakeServer.Status(HttpStatusCode.NoContent);
        }

        _uploads.TryRemove(uploadId, out _);
        if (request.Method == HttpMethod.Post)
        {
            var complete = JsonSerializer.Deserialize<LibraryUploadComplete>(body, ServerClient.Json)!;
            if (upload.Data.Length != upload.Start.Length)
            {
                return FakeServer.Status(HttpStatusCode.BadRequest, "The upload is not complete.");
            }

            if (Refuse?.Invoke(upload.Start.Path) is { } refused)
            {
                return refused;
            }

            if (complete.OperationId is { } operation
                && (!Marks.TryGetValue(upload.Start.Location + "|" + upload.Start.Path, out var holder) || holder != operation))
            {
                return FakeServer.Status(HttpStatusCode.Conflict, "The file is held by another operation.");
            }

            Files.Enqueue(new LibraryFile(upload.LibraryId, upload.Start.Location, upload.Start.Path, upload.Start.WorkerId, complete.ShortcutTarget, upload.Data.ToArray(), complete.Sha256, complete.ContentLength));
        }

        return FakeServer.Status(HttpStatusCode.NoContent);
    }
}

/// <summary>A file the worker wrote into a folder of a library.</summary>
internal sealed record LibraryFile(Guid LibraryId, string? Location, string Path, Guid WorkerId, string? ShortcutTarget, byte[] Content, string? Sha256 = null, long? ContentLength = null);
