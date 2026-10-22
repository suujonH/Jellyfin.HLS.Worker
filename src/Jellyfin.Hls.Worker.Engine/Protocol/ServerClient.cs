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
    public const int ProtocolVersion = 1;

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
            $"Client=\"Jellyfin HLS Worker\", Device=\"{Environment.MachineName}\", DeviceId=\"jellyfin-hls-worker\", Version=\"1.0.0\", Token=\"{apiKey}\"");
        _controlTimeout = controlTimeout;
        _control = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _control.Timeout = Timeout.InfiniteTimeSpan;

        // Redirects are followed here, so a redirect never carries the credentials.
        _download = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30) })
            : new HttpClient(handler, disposeHandler: false);
        _download.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>Gets the JSON options of the protocol: the server's PascalCase names, enums as names.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
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

    /// <summary>Claims a job.</summary>
    /// <param name="worker">The name of the worker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The job, or <c>null</c> when there is none.</returns>
    public Task<WorkAssignment?> PollAsync(string worker, CancellationToken cancellationToken)
        => SendAsync<WorkAssignment>(HttpMethod.Post, "HlsWorker/Poll", new WorkerRequest(worker), cancellationToken);

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
        if (body is not null)
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
