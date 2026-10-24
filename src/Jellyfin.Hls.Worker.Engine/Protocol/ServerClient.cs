using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Hls.Worker.Protocol;

/// <summary>
/// Speaks the HLS worker protocol with a Jellyfin server. Every request below the server's address carries the API key and the
/// protocol header; the redirects of a download are followed without them.
/// </summary>
public sealed class ServerClient : IDisposable
{
    /// <summary>The protocol version this worker speaks.</summary>
    public const int ProtocolVersion = 3;

    /// <summary>The header that names the protocol version.</summary>
    public const string ProtocolHeader = "X-Jellyfin-Hls-Protocol";

    private const int MaxRedirects = 5;

    // A download has no deadline as a whole, but one that sends nothing for this long is given up.
    private static readonly TimeSpan _downloadIdleTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _control;
    private readonly HttpClient _download;
    private readonly Uri _baseAddress;
    private readonly AuthenticationHeaderValue _authorization;
    private readonly TimeSpan _controlTimeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerClient"/> class.
    /// </summary>
    /// <param name="server">The address of the server, with the path of a reverse proxy if any.</param>
    /// <param name="apiKey">An API key of the server.</param>
    /// <param name="controlTimeout">The timeout of a request that is not a download.</param>
    /// <param name="handler">The handler for tests; it serves every request.</param>
    public ServerClient(Uri server, string apiKey, TimeSpan controlTimeout, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        _baseAddress = new Uri(server.AbsoluteUri.TrimEnd('/') + "/");
        _authorization = new AuthenticationHeaderValue(
            "MediaBrowser",
            $"Client=\"Jellyfin HLS Worker\", Device=\"{Environment.MachineName}\", DeviceId=\"jellyfin-hls-worker\", Version=\"0.01\", Token=\"{apiKey}\"");
        _controlTimeout = controlTimeout;
        _control = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _control.Timeout = Timeout.InfiniteTimeSpan;

        // Redirects are followed here, so a redirect never carries the credentials.
        _download = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30) })
            : new HttpClient(handler, disposeHandler: false);
        _download.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>Gets the JSON options of the protocol: the server's PascalCase names, enums as names, GUIDs as 32 digits.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new GuidConverter() }
    };

    /// <summary>Gets the address of the server, with a trailing slash.</summary>
    public Uri BaseAddress => _baseAddress;

    /// <summary>
    /// Checks that the server speaks the protocol of this worker.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="ServerException">The server speaks another protocol or refuses the key.</exception>
    public async Task CheckProtocolAsync(CancellationToken cancellationToken)
    {
        var version = await SendAsync<ProtocolVersion>(HttpMethod.Get, "HlsWorker/Protocol", null, cancellationToken).ConfigureAwait(false);
        if (version?.Version != ProtocolVersion)
        {
            throw new ServerException(
                HttpStatusCode.OK,
                $"服务器的 HLS Worker 协议版本为 {version?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "未知"}，本 Worker 需要 {ProtocolVersion}。");
        }
    }

    /// <summary>Gets the id of the worker: the one it has, which the server records when it does not know it, or a new one.</summary>
    /// <param name="id">The id the worker has; <c>null</c> for a new one.</param>
    /// <param name="name">The name of the worker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The id.</returns>
    public async Task<Guid> RegisterWorkerAsync(Guid? id, string name, CancellationToken cancellationToken)
        => (await SendAsync<WorkerRegistered>(HttpMethod.Post, "HlsWorker/Workers", new WorkerRegistration(id, name), cancellationToken).ConfigureAwait(false)
            ?? throw new ServerException(HttpStatusCode.NoContent, "服务器没有返回 Worker Id。")).Id;

    /// <summary>Claims a job.</summary>
    /// <param name="request">The worker and the jobs it takes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The job, or <c>null</c> when there is none.</returns>
    public Task<WorkAssignment?> PollAsync(WorkerRequest request, CancellationToken cancellationToken)
        => SendAsync<WorkAssignment>(HttpMethod.Post, "HlsWorker/Poll", request, cancellationToken);

    /// <summary>Gives back a job before it is identified, without counting the attempt: the worker cannot read its input.</summary>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseToken">The lease token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task ReturnAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken)
        => SendAsync<object>(HttpMethod.Post, JobPath(jobId, "Return"), new LeaseRequest(leaseToken), cancellationToken);

    /// <summary>Starts writing a file into a folder of a library.</summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The upload.</returns>
    public async Task<string> StartUploadAsync(Guid libraryId, LibraryUploadStart request, CancellationToken cancellationToken)
        => (await SendAsync<LibraryUpload>(HttpMethod.Post, LibraryPath(libraryId) + "/Uploads", request, cancellationToken).ConfigureAwait(false)
            ?? throw new ServerException(HttpStatusCode.NoContent, "服务器没有返回上传。")).UploadId;

    /// <summary>Writes a chunk of an upload; a chunk sent again at the same offset replaces what follows it.</summary>
    /// <param name="uploadId">The upload.</param>
    /// <param name="offset">Where the chunk goes.</param>
    /// <param name="chunk">The chunk.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task PutChunkAsync(string uploadId, long offset, ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        var content = new ReadOnlyMemoryContent(chunk);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return SendAsync<object>(
            HttpMethod.Put,
            UploadPath(uploadId) + "?offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            content,
            cancellationToken);
    }

    /// <summary>Ends an upload: the file is put in place, or the TrueCache entry of the zip and then the .strm file.</summary>
    /// <param name="uploadId">The upload.</param>
    /// <param name="shortcutTarget">The target of the .strm file when the upload is the zip of its entry; <c>null</c> for a file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="operationId">The operation that holds the file; the server answers 409 when it no longer does.</param>
    /// <param name="content">The hash and size of the media content the file is, for the server to link it.</param>
    /// <returns>A task.</returns>
    public Task CompleteUploadAsync(string uploadId, string? shortcutTarget, CancellationToken cancellationToken, string? operationId = null, (string Sha256, long Length)? content = null)
        => SendAsync<object>(
            HttpMethod.Post,
            UploadPath(uploadId) + "/Complete",
            new LibraryUploadComplete(shortcutTarget, operationId, content?.Sha256, content?.Length),
            cancellationToken);

    /// <summary>Marks a file the worker adds to a library as held by one of its operations, whatever held it before.</summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="claim">The file and the operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation that held the file before, or <c>null</c>.</returns>
    public async Task<string?> ClaimWorkingAsync(Guid libraryId, WorkingClaim claim, CancellationToken cancellationToken)
        => (await SendAsync<WorkingState>(HttpMethod.Put, LibraryPath(libraryId) + "/Working", claim, cancellationToken).ConfigureAwait(false))?.OperationId;

    /// <summary>Gets the operation that holds a file the worker adds to a library.</summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation, or <c>null</c> when the file is not marked.</returns>
    public async Task<string?> ReadWorkingAsync(Guid libraryId, WorkingPath path, CancellationToken cancellationToken)
        => (await SendAsync<WorkingState>(HttpMethod.Post, LibraryPath(libraryId) + "/Working/Read", path, cancellationToken).ConfigureAwait(false))?.OperationId;

    /// <summary>Removes the mark of a file while it names the operation.</summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="release">The file and the operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task ReleaseWorkingAsync(Guid libraryId, WorkingRelease release, CancellationToken cancellationToken)
        => SendAsync<object>(HttpMethod.Post, LibraryPath(libraryId) + "/Working/Release", release, cancellationToken);

    /// <summary>Drops an upload.</summary>
    /// <param name="uploadId">The upload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken)
        => SendAsync<object>(HttpMethod.Delete, UploadPath(uploadId), null, cancellationToken);

    /// <summary>Gets the libraries that are pre-encoded.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries.</returns>
    public async Task<IReadOnlyList<WorkerLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
        => await SendAsync<List<WorkerLibrary>>(HttpMethod.Get, "HlsWorker/Libraries", null, cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>Checks an HLS root as the server sees it.</summary>
    /// <param name="hlsBase">The root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public async Task<StoreTestResult> CheckBaseAsync(string hlsBase, CancellationToken cancellationToken)
        => await SendAsync<StoreTestResult>(HttpMethod.Post, "HlsWorker/Bases/Check", new BaseRequest(hlsBase), cancellationToken).ConfigureAwait(false)
            ?? throw new ServerException(HttpStatusCode.NoContent, "服务器没有返回 HLS 根的检查结果。");

    /// <summary>Starts a job for a content the worker adds.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The answer.</returns>
    public async Task<EagerResponse> StartEagerJobAsync(EagerRequest request, CancellationToken cancellationToken)
        => await SendAsync<EagerResponse>(HttpMethod.Post, "HlsWorker/Jobs/Eager", request, cancellationToken).ConfigureAwait(false)
            ?? throw new ServerException(HttpStatusCode.NoContent, "服务器没有返回预编码任务。");

    /// <summary>Gets the libraries of the server with their folders.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The libraries.</returns>
    public async Task<IReadOnlyList<VirtualFolder>> GetVirtualFoldersAsync(CancellationToken cancellationToken)
        => await SendAsync<List<VirtualFolder>>(HttpMethod.Get, "Library/VirtualFolders", null, cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>Gets the accesses of the network storage of the server.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The accesses, without secrets.</returns>
    public async Task<IReadOnlyList<NetworkStorageAccess>> GetAccessesAsync(CancellationToken cancellationToken)
        => await SendAsync<List<NetworkStorageAccess>>(HttpMethod.Get, "NetworkStorage/Accesses", null, cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>Adds an access to the network storage of the server.</summary>
    /// <param name="access">The access, with its secret.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task AddAccessAsync(NetworkStorageAccess access, CancellationToken cancellationToken)
        => SendAsync<object>(HttpMethod.Post, "NetworkStorage/Accesses", access, cancellationToken);

    /// <summary>Asks the server to scan a library for new and changed files.</summary>
    /// <param name="itemId">The item of the library.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task RefreshAsync(Guid itemId, CancellationToken cancellationToken)
        => SendAsync<object>(HttpMethod.Post, "Items/" + itemId.ToString("N") + "/Refresh?metadataRefreshMode=Default&imageRefreshMode=Default", null, cancellationToken);

    /// <summary>Extends the lease of a job.</summary>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseToken">The lease token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task HeartbeatAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken)
        => SendAsync<object>(HttpMethod.Post, JobPath(jobId, "Heartbeat"), new LeaseRequest(leaseToken), cancellationToken);

    /// <summary>Reports what the input is.</summary>
    /// <param name="jobId">The job.</param>
    /// <param name="identification">The identification.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The answer.</returns>
    public async Task<IdentifyResponse> IdentifyAsync(Guid jobId, Identification identification, CancellationToken cancellationToken)
        => await SendAsync<IdentifyResponse>(HttpMethod.Post, JobPath(jobId, "Identify"), identification, cancellationToken).ConfigureAwait(false)
            ?? throw new ServerException(HttpStatusCode.NoContent, "服务器没有返回识别结果。");

    /// <summary>Reports the renditions of a job.</summary>
    /// <param name="jobId">The job.</param>
    /// <param name="completion">The renditions.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task CompleteAsync(Guid jobId, Completion completion, CancellationToken cancellationToken)
        => SendAsync<object>(HttpMethod.Post, JobPath(jobId, "Complete"), completion, cancellationToken);

    /// <summary>Reports a failed job.</summary>
    /// <param name="jobId">The job.</param>
    /// <param name="failure">The failure.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task FailAsync(Guid jobId, Failure failure, CancellationToken cancellationToken)
        => SendAsync<object>(HttpMethod.Post, JobPath(jobId, "Failed"), failure, cancellationToken);

    /// <summary>
    /// Downloads the input of a job, hashing it on the way.
    /// </summary>
    /// <param name="url">The address the server gave, relative to it or absolute.</param>
    /// <param name="destination">The file to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The SHA-256 of what was written, in lower-case hexadecimal.</returns>
    public async Task<string> DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseAddress, url);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (_baseAddress.IsBaseOf(uri))
            {
                AddHeaders(request);
            }

            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(_downloadIdleTimeout);
            HttpResponseMessage response;
            try
            {
                response = await _download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ServerException(null, "下载地址在 " + _downloadIdleTimeout.TotalSeconds + " 秒内没有应答：" + uri.GetLeftPart(UriPartial.Path));
            }

            using var answer = response;
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                if (hop >= MaxRedirects)
                {
                    throw new ServerException(response.StatusCode, "下载地址跳转次数过多。");
                }

                uri = new Uri(uri, location);
                continue;
            }

            try
            {
                await EnsureSuccessAsync(response, idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ServerException(null, "下载地址返回 HTTP " + (int)response.StatusCode + "，之后没有应答。");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var file = File.Create(destination);
            await using (file.ConfigureAwait(false))
            {
                var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    try
                    {
                        while (true)
                        {
                            idle.CancelAfter(_downloadIdleTimeout);
                            if ((read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) == 0)
                            {
                                break;
                            }

                            hash.AppendData(buffer, 0, read);
                            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new ServerException(null, "下载在 " + _downloadIdleTimeout.TotalSeconds + " 秒内没有收到数据。");
                    }
                }
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _control.Dispose();
        _download.Dispose();
    }

    private static string JobPath(Guid jobId, string action) => "HlsWorker/Jobs/" + jobId.ToString("N") + "/" + action;

    private static string LibraryPath(Guid libraryId) => "HlsWorker/Libraries/" + libraryId.ToString("N");

    private static string UploadPath(string uploadId) => "HlsWorker/Uploads/" + Uri.EscapeDataString(uploadId);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // The server explains a refusal in the body, as a string or a problem.
        var body = string.Empty;
        try
        {
            body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (HttpRequestException)
        {
        }

        if (body.Length > 500)
        {
            body = body[..500];
        }

        throw new ServerException(response.StatusCode, $"服务器返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}{(body.Length > 0 ? "：" + body : string.Empty)}");
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.Authorization = _authorization;
        request.Headers.Add(ProtocolHeader, ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_controlTimeout);
        using var request = new HttpRequestMessage(method, new Uri(_baseAddress, path));
        AddHeaders(request);
        if (body is HttpContent content)
        {
            request.Content = content;
        }
        else if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        try
        {
            using var response = await _control.SendAsync(request, timeout.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServerException(null, "服务器在 " + _controlTimeout.TotalSeconds + " 秒内没有应答：" + path);
        }
    }
}
