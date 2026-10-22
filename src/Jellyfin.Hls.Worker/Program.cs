using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Hls.Worker;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

const int Usage = 2;
var version = typeof(WorkerEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine($"""
        Jellyfin HLS Worker {version}
        用法：
          jellyfin-hls-worker init  [--config worker.json]
          jellyfin-hls-worker check [--config worker.json]
          jellyfin-hls-worker run   [--config worker.json] [--once]
          jellyfin-hls-worker add   [--config worker.json] --library <名称或 Id> [--into <目录>]
                                    [--connection <S3 连接> | --direct] [--encode <档位,…> | --encode all] <文件或目录>...
        init 生成配置文件模板；check 实际测试编码器、服务器、各连接和 HLS 根，不领取任务；run 常驻领取并处理任务。
        --once 在没有可领取的任务、且进行中的任务都结束后退出。
        add 把本机文件添加到媒体库：视频按媒体库的默认（Libraries）放到 S3（.strm 和 TrueCache 条目写进媒体库目录）
        或直接放进媒体库目录，其他文件原样复制；目录连同目录名添加。--encode 随添加编码这些档位，其余在扫描入库后编码。
        API Key 也可以由环境变量 JELLYFIN_HLS_API_KEY 提供。Ctrl+C / SIGTERM 停止进行中的任务并退出。
        退出码：0 成功，1 有任务失败或服务器不可用，2 参数或配置错误，130 被停止。
        """);
    return 0;
}

if (args is ["--version"])
{
    Console.WriteLine(version);
    return 0;
}

var command = args[0];
if (command is not ("init" or "check" or "run" or "add"))
{
    Console.Error.WriteLine("命令应为 init、check、run 或 add；--help 查看用法。");
    return Usage;
}

var config = "worker.json";
var once = false;
string? library = null;
var into = string.Empty;
string? connection = null;
string? encode = null;
var paths = new List<string>();
for (var i = 1; i < args.Length; i++)
{
    var hasValue = i + 1 < args.Length;
    if (args[i] == "--config" && hasValue)
    {
        config = args[++i];
    }
    else if (args[i] == "--once" && command == "run")
    {
        once = true;
    }
    else if (command == "add" && args[i] == "--library" && hasValue)
    {
        library = args[++i];
    }
    else if (command == "add" && args[i] == "--into" && hasValue)
    {
        into = args[++i];
    }
    else if (command == "add" && args[i] == "--connection" && hasValue)
    {
        connection = args[++i];
    }
    else if (command == "add" && args[i] == "--direct")
    {
        connection = string.Empty;
    }
    else if (command == "add" && args[i] == "--encode" && hasValue)
    {
        encode = args[++i];
    }
    else if (command == "add" && !args[i].StartsWith("--", StringComparison.Ordinal))
    {
        paths.Add(args[i]);
    }
    else
    {
        Console.Error.WriteLine("未知或缺少参数的选项：" + args[i]);
        return Usage;
    }
}

if (command == "add" && (library is null || paths.Count == 0))
{
    Console.Error.WriteLine("add 需要 --library 和至少一个文件或目录。");
    return Usage;
}

if (command == "init")
{
    var path = Path.GetFullPath(config);
    if (File.Exists(path))
    {
        Console.Error.WriteLine("配置文件已存在：" + path);
        return Usage;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    new WorkerSettings
    {
        Connections = [new ConnectionSettings { Name = "hls", Type = ConnectionType.Local, LocalRoot = "/mnt/hls" }],
        Hls = new HlsTargetSettings { Connection = "hls" }
    }.Save(path);
    Console.WriteLine($"已创建 {path}。填写 Server、ApiKey、连接和 Hls 后运行 check。");
    return 0;
}

WorkerSettings settings;
try
{
    settings = WorkerSettings.Load(config);
    if (settings.Hls is null)
    {
        throw new ArgumentException("需要配置 Hls：写入 HLS 的连接和目录。");
    }
}
catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return Usage;
}

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddFilter("System", LogLevel.Warning)
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        options.ColorBehavior = LoggerColorBehavior.Disabled;
    }));
var logger = loggerFactory.CreateLogger("Jellyfin.Hls.Worker");

using var stop = new CancellationTokenSource();
void Stop()
{
    if (!stop.IsCancellationRequested)
    {
        logger.LogInformation("收到停止信号");
        stop.Cancel();
    }
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Stop();
};
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    Stop();
});

try
{
    using var engine = new WorkerEngine(settings, loggerFactory);
    if (command == "check")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await Preflight.CheckAsync(settings, engine, logger, timeout.Token);
        logger.LogInformation("检查通过");
        return WorkerEngine.Success;
    }

    if (command == "add")
    {
        var request = await CreateAddRequestAsync(engine, library!, into, connection, encode, paths, stop.Token);
        var result = await engine.AddAsync(request, stop.Token);
        return result.Failed == 0 ? WorkerEngine.Success : WorkerEngine.Failed;
    }

    return await engine.RunAsync(once, stop.Token);
}
catch (ArgumentException ex)
{
    logger.LogError("{Message}", ex.Message);
    return Usage;
}
catch (OperationCanceledException)
{
    logger.LogError("已停止或超时");
    return WorkerEngine.Stopped;
}
catch (Exception ex)
{
    logger.LogError("{Message}", ex.Message);
    return WorkerEngine.Failed;
}

// The library by its name or its id; "all" encodes every quality of the library.
static async Task<AddRequest> CreateAddRequestAsync(
    WorkerEngine engine,
    string library,
    string into,
    string? connection,
    string? encode,
    List<string> paths,
    CancellationToken cancellationToken)
{
    var folders = await engine.Server.GetVirtualFoldersAsync(cancellationToken);
    var folder = folders.FirstOrDefault(f => string.Equals(f.Name, library, StringComparison.OrdinalIgnoreCase))
        ?? folders.FirstOrDefault(f => Guid.TryParse(library, out var wanted) && Guid.TryParse(f.ItemId, out var id) && id.Equals(wanted))
        ?? throw new ArgumentException("服务器上没有媒体库 " + library + "。可用：" + string.Join("、", folders.Select(f => f.Name)));
    var id = Guid.Parse(folder.ItemId);
    IReadOnlyList<int> qualities = [];
    if (string.Equals(encode, "all", StringComparison.OrdinalIgnoreCase))
    {
        qualities = (await engine.Server.GetLibrariesAsync(cancellationToken)).FirstOrDefault(l => l.Id.Equals(id))?.Qualities
            ?? throw new ArgumentException("媒体库 " + folder.Name + " 没有启用预编码。");
    }
    else if (encode is not null)
    {
        qualities = encode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(q => int.TryParse(q.TrimEnd('p'), System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException("--encode 的档位无效：" + q))
            .ToList();
    }

    return new AddRequest(id, paths, into, connection, qualities);
}
