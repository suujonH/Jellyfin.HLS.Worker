using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>
/// The settings of a worker, kept in one JSON file.
/// </summary>
public sealed class WorkerSettings
{
    /// <summary>The H.264 encoders the worker can drive.</summary>
    public static readonly IReadOnlyList<string> Encoders = ["libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_vaapi"];

    private static readonly JsonSerializerOptions _json = new()
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

    /// <summary>Gets or sets the ffmpeg executable.</summary>
    public string Ffmpeg { get; set; } = "ffmpeg";

    /// <summary>Gets or sets the ffprobe executable.</summary>
    public string Ffprobe { get; set; } = "ffprobe";

    /// <summary>Gets or sets the TrueCache tool, which writes the entries of the media put on S3.</summary>
    public string TrueCacheTool { get; set; } = "Jellyfin.TrueCache.Tool";

    /// <summary>Gets or sets the folder of the inputs and the jobs.</summary>
    public string WorkDirectory { get; set; } = "work";

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

    /// <summary>Gets or sets where the renditions are written.</summary>
    public HlsTargetSettings? Hls { get; set; }

    /// <summary>Gets or sets where the media added to each library goes by default.</summary>
    public List<LibrarySettings> Libraries { get; set; } = [];

    /// <summary>Gets the folder of the cached inputs.</summary>
    [JsonIgnore]
    public string InputDirectory => Path.Combine(WorkDirectory, "inputs");

    /// <summary>Gets the folder of the jobs.</summary>
    [JsonIgnore]
    public string JobDirectory => Path.Combine(WorkDirectory, "jobs");

    /// <summary>Gets the database of the media this worker added or downloaded.</summary>
    [JsonIgnore]
    public string DatabasePath => Path.Combine(WorkDirectory, "worker.db");

    /// <summary>
    /// Reads and validates the settings. Relative paths are relative to the folder of the file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentException">The settings are not valid.</exception>
    public static WorkerSettings Load(string path)
    {
        var settings = Read(path);
        if (Environment.GetEnvironmentVariable("JELLYFIN_HLS_API_KEY") is { Length: > 0 } apiKey)
        {
            settings.ApiKey = apiKey;
        }

        settings.Resolve(Path.GetDirectoryName(Path.GetFullPath(path))!);
        settings.Validate();
        return settings;
    }

    /// <summary>
    /// Reads the settings as they are written, to be edited and saved again: relative paths are kept, the API key of the
    /// environment is not used, and nothing is checked.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentException">The file is not of settings.</exception>
    public static WorkerSettings Read(string path)
    {
        WorkerSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<WorkerSettings>(File.ReadAllText(path), _json) ?? throw new ArgumentException("配置文件为空。");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("配置文件无效：" + ex.Message, ex);
        }

        // A list written as null is an empty one; an item written as null is no setting; a text written as null is the default.
        settings.Connections = settings.Connections?.Where(c => c is not null).ToList() ?? [];
        settings.Libraries = settings.Libraries?.Where(l => l is not null).ToList() ?? [];
        FillNulls(settings);
        settings.Connections.ForEach(FillNulls);
        settings.Libraries.ForEach(FillNulls);
        if (settings.Hls is not null)
        {
            FillNulls(settings.Hls);
        }

        return settings;
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

    /// <summary>
    /// Writes the settings.
    /// </summary>
    /// <param name="path">The file.</param>
    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, _json));
        File.Move(temporary, path, true);
    }

    /// <summary>
    /// Finds a connection by its name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The connection.</returns>
    /// <exception cref="ArgumentException">There is no such connection.</exception>
    public ConnectionSettings GetConnection(string name)
        => Connections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
            ?? throw new ArgumentException("没有名为 " + name + " 的连接。");

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

        if (Connections is null || Connections.Any(c => c is null))
        {
            throw new ArgumentException("Connections 应为对象数组。");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in Connections)
        {
            connection.Validate();
            if (!names.Add(connection.Name))
            {
                throw new ArgumentException("连接名称重复：" + connection.Name);
            }
        }

        if (Hls is not null)
        {
            Hls.Validate(GetConnection(Hls.Connection));
        }

        if (Libraries is null || Libraries.Any(l => l is null))
        {
            throw new ArgumentException("Libraries 应为对象数组。");
        }

        if (Libraries.GroupBy(l => l.Id).FirstOrDefault(g => g.Count() > 1) is { } repeated)
        {
            throw new ArgumentException("媒体库重复：" + repeated.Key);
        }

        foreach (var library in Libraries)
        {
            library.Validate(this);
        }
    }

    /// <summary>
    /// Gets the default of a library, or one that puts the media directly into its folder.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>The default.</returns>
    public LibrarySettings GetLibrary(Guid libraryId)
        => Libraries.FirstOrDefault(l => l.Id.Equals(libraryId)) ?? new LibrarySettings { Id = libraryId };

    private void Resolve(string directory)
    {
        WorkDirectory = Path.GetFullPath(WorkDirectory, directory);
        Ffmpeg = ResolveProgram(Ffmpeg, directory);
        Ffprobe = ResolveProgram(Ffprobe, directory);
        TrueCacheTool = ResolveProgram(TrueCacheTool, directory);
        foreach (var connection in Connections ?? [])
        {
            if (connection?.Type == ConnectionType.Local && !string.IsNullOrWhiteSpace(connection.LocalRoot))
            {
                connection.LocalRoot = Path.GetFullPath(connection.LocalRoot, directory);
            }

            if (connection?.Type == ConnectionType.Sftp && !string.IsNullOrWhiteSpace(connection.PrivateKeyPath))
            {
                connection.PrivateKeyPath = Path.GetFullPath(connection.PrivateKeyPath, directory);
            }
        }
    }

    // A bare name is looked up on the PATH; anything with a separator is a path.
    private static string ResolveProgram(string program, string directory)
        => program.Contains('/', StringComparison.Ordinal) || program.Contains('\\', StringComparison.Ordinal) ? Path.GetFullPath(program, directory) : program;
}
