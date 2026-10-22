using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Hls.Worker;
using Jellyfin.Hls.Worker.Configuration;
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
        init 生成配置文件模板；check 实际测试编码器、服务器、各连接和 HLS 根，不领取任务；run 常驻领取并处理任务。
        --once 在没有可领取的任务、且进行中的任务都结束后退出。
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
if (command is not ("init" or "check" or "run"))
{
    Console.Error.WriteLine("命令应为 init、check 或 run；--help 查看用法。");
    return Usage;
}

var config = "worker.json";
var once = false;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--config" && i + 1 < args.Length)
    {
        config = args[++i];
    }
    else if (args[i] == "--once" && command == "run")
    {
        once = true;
    }
    else
    {
        Console.Error.WriteLine("未知或缺少参数的选项：" + args[i]);
        return Usage;
    }
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

    return await engine.RunAsync(once, stop.Token);
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
