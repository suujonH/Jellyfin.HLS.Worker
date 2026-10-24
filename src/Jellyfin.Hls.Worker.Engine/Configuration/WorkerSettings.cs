using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>
/// The settings of a worker, kept by <see cref="SettingsStore"/>.
/// </summary>
public sealed class WorkerSettings
{
    /// <summary>The H.264 encoders the worker can drive.</summary>
    public static readonly IReadOnlyList<string> Encoders = ["libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_vaapi"];

    /// <summary>Gets the JSON options the settings are stored with.</summary>
    internal static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Gets or sets the address of the server, with the path of a reverse proxy if any.</summary>
    public string Server { get; set; } = "http://localhost:8096";

    /// <summary>Gets or sets an API key of the server; the environment variable JELLYFIN_HLS_API_KEY takes precedence.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the name the worker reports.</summary>
    public string Name { get; set; } = Environment.MachineName;

    /// <summary>Gets or sets the id the server knows the worker by; <see cref="Guid.Empty"/> until the server gave one.</summary>
    public Guid WorkerId { get; set; }

    /// <summary>Gets or sets the folder of the inputs and the jobs; empty for <see cref="WorkerPaths.DefaultTempDirectory"/>.</summary>
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the H.264 encoder.</summary>
    public string Encoder { get; set; } = "libx264";

    /// <summary>Gets or sets the device of a hardware encoder: the render node for VAAPI, the GPU index for NVENC.</summary>
    public string HardwareDevice { get; set; } = string.Empty;

    /// <summary>Gets or sets the threads of ffmpeg, 0 for its choice.</summary>
    public int Threads { get; set; }

    /// <summary>Gets or sets how many jobs read, identify and encode at the same time.</summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>Gets or sets how many jobs upload at the same time.</summary>
    public int UploadConcurrency { get; set; } = 1;

    /// <summary>Gets or sets how many HLS files the worker writes at the same time, over every job and directory.</summary>
    public int UploadFileConcurrency { get; set; } = 4;

    /// <summary>Gets or sets the seconds between two polls that found nothing.</summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>Gets or sets the timeout of a request that is not a download, in seconds.</summary>
    public int ControlTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the size of the input cache in GiB, 0 to free an input once its job is done.</summary>
    public double InputCacheGiB { get; set; } = 20;

    /// <summary>Gets or sets a value indicating whether the folder of a failed job is kept.</summary>
    public bool KeepFailedJobs { get; set; }

    /// <summary>Gets or sets the connections to storage.</summary>
    public List<ConnectionSettings> Connections { get; set; } = [];

    /// <summary>Gets or sets where the files of a library are saved.</summary>
    public List<SchemeSettings> Schemes { get; set; } = [];

    /// <summary>Gets or sets the defaults of the libraries.</summary>
    public List<LibrarySettings> Libraries { get; set; } = [];

    /// <summary>Gets or sets the libraries the server pre-encodes, and whether their jobs are claimed.</summary>
    public List<PreEncodeSettings> PreEncodes { get; set; } = [];

    /// <summary>Gets the folder of the cached inputs.</summary>
    [JsonIgnore]
    public string InputDirectory => Path.Combine(TempDirectory, "inputs");

    /// <summary>Gets the folder of the jobs.</summary>
    [JsonIgnore]
    public string JobDirectory => Path.Combine(TempDirectory, "jobs");

    /// <summary>Gets the folder of the renditions declared on this machine, by a scheme that declares no HLS directory; never cleaned up.</summary>
    [JsonIgnore]
    public string LocalHlsDirectory => Path.Combine(TempDirectory, "hls");

    /// <summary>
    /// Finds a connection by its id.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The connection.</returns>
    /// <exception cref="ArgumentException">There is no such connection.</exception>
    public ConnectionSettings GetConnection(Guid id)
        => Connections.FirstOrDefault(c => c.Id.Equals(id)) ?? throw new ArgumentException("没有这个远程目录。");

    /// <summary>
    /// Finds a scheme by its id.
    /// </summary>
    /// <param name="id">The id; <c>null</c> for none.</param>
    /// <returns>The scheme, or <c>null</c>.</returns>
    public SchemeSettings? GetScheme(Guid? id)
        => id is { } value ? Schemes.FirstOrDefault(s => s.Id.Equals(value)) : null;

    /// <summary>
    /// Gets the defaults of a library, or empty ones, which are not added to <see cref="Libraries"/>.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>The defaults.</returns>
    public LibrarySettings GetLibrary(Guid libraryId)
        => Libraries.FirstOrDefault(l => l.Id.Equals(libraryId)) ?? new LibrarySettings { Id = libraryId };

    /// <summary>
    /// Gets the qualities of the server the worker does not encode for a library.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>The qualities turned off; none for a library never refreshed.</returns>
    public IReadOnlyList<int> GetDisabledQualities(Guid libraryId)
        => PreEncodes.Find(p => p.LibraryId.Equals(libraryId))?.DisabledQualities ?? [];

    /// <summary>
    /// Leaves out of qualities of the server those the worker does not encode for a library.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="qualities">The qualities of the server.</param>
    /// <returns>The qualities the worker encodes, in the same order.</returns>
    public IReadOnlyList<int> FilterQualities(Guid libraryId, IEnumerable<int> qualities)
    {
        var disabled = GetDisabledQualities(libraryId);
        return [.. qualities.Where(q => !disabled.Contains(q))];
    }

    /// <summary>
    /// Checks the settings.
    /// </summary>
    /// <exception cref="ArgumentException">The settings are not valid.</exception>
    public void Validate()
    {
        if (!Uri.TryCreate(Server, UriKind.Absolute, out var server)
            || server.Scheme is not ("http" or "https")
            || server.Query.Length != 0
            || server.Fragment.Length != 0
            || server.UserInfo.Length != 0)
        {
            throw new ArgumentException("Server 应为 Jellyfin 的 http(s) 地址，可以带反向代理的路径。");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("需要 ApiKey（或环境变量 JELLYFIN_HLS_API_KEY）：在 Jellyfin 的控制台 → API 密钥中创建。");
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 128)
        {
            throw new ArgumentException("Name 的长度应为 1–128。");
        }

        if (Concurrency is < 1 or > 32 || UploadConcurrency is < 1 or > 32)
        {
            throw new ArgumentException("Concurrency 和 UploadConcurrency 应为 1–32。");
        }

        if (UploadFileConcurrency is < 1 or > 64)
        {
            throw new ArgumentException("UploadFileConcurrency 应为 1–64。");
        }

        if (Threads is < 0 or > 256 || PollSeconds is < 1 or > 3600 || ControlTimeoutSeconds is < 1 or > 600)
        {
            throw new ArgumentException("Threads 应为 0–256，PollSeconds 应为 1–3600，ControlTimeoutSeconds 应为 1–600。");
        }

        if (!double.IsFinite(InputCacheGiB) || InputCacheGiB is < 0 or > 65536)
        {
            throw new ArgumentException("InputCacheGiB 应为 0–65536。");
        }

        if (!Encoders.Contains(Encoder))
        {
            throw new ArgumentException("Encoder 应为 " + string.Join("、", Encoders) + " 之一。");
        }

        if (Encoder == "h264_vaapi" && HardwareDevice.Length == 0)
        {
            throw new ArgumentException("h264_vaapi 需要 HardwareDevice，例如 /dev/dri/renderD128。");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<Guid> { SchemeEntry.JellyfinLibrary };
        foreach (var connection in Connections)
        {
            connection.Validate();
            if (!names.Add(connection.Name))
            {
                throw new ArgumentException("远程目录名称重复：" + connection.Name);
            }

            if (!ids.Add(connection.Id))
            {
                throw new ArgumentException("远程目录 Id 重复：" + connection.Name);
            }
        }

        // Schemes are referred to by their ids: two can have the same name.
        ids.Clear();
        foreach (var scheme in Schemes)
        {
            scheme.Validate(this);
            if (scheme.Id.Equals(Guid.Empty) || !ids.Add(scheme.Id))
            {
                throw new ArgumentException("方案 " + scheme.Name + " 的 Id 为空或重复。");
            }
        }

        if (Libraries.GroupBy(l => l.Id).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new ArgumentException("媒体库重复：" + repeated.Key);
        }

        foreach (var library in Libraries)
        {
            library.Validate(this);
        }

        if (PreEncodes.GroupBy(l => l.LibraryId).FirstOrDefault(g => g.Count() > 1) is { } repeatedPreEncode)
        {
            throw new ArgumentException("预编码媒体库重复：" + repeatedPreEncode.Key);
        }
    }

    /// <summary>
    /// Mends settings as they were read: a list written as null is an empty one, an item written as null is no setting, and a text
    /// written as null is the default.
    /// </summary>
    /// <param name="settings">The settings.</param>
    internal static void Normalize(WorkerSettings settings)
    {
        FillNulls(settings);
        settings.Connections = settings.Connections?.Where(c => c is not null).ToList() ?? [];
        settings.Schemes = settings.Schemes?.Where(s => s is not null).ToList() ?? [];
        settings.Libraries = settings.Libraries?.Where(l => l is not null).ToList() ?? [];
        settings.PreEncodes = settings.PreEncodes?.Where(p => p is not null).ToList() ?? [];
        settings.Connections.ForEach(FillNulls);
        foreach (var scheme in settings.Schemes)
        {
            FillNulls(scheme);
            scheme.Hls = scheme.Hls?.Where(e => e is not null).ToList() ?? [];
            scheme.Sources = scheme.Sources?.Where(e => e is not null).ToList() ?? [];
        }

        foreach (var library in settings.Libraries)
        {
            FillNulls(library);
            library.Locations = library.Locations?.Where(l => l is not null).ToList() ?? [];
            library.PreEncodeQualities ??= [];
        }

        foreach (var preEncode in settings.PreEncodes)
        {
            FillNulls(preEncode);
            preEncode.Qualities ??= [];
            preEncode.DisabledQualities ??= [];
        }
    }

    private static void FillNulls<T>(T item)
        where T : new()
    {
        var defaults = new T();
        foreach (var property in typeof(T).GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
        {
            if (property.GetValue(item) is null)
            {
                property.SetValue(item, property.GetValue(defaults));
            }
        }
    }
}
