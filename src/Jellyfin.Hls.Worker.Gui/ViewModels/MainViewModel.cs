using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The window: the settings being edited, the worker running its jobs, the media being added, and the log.
/// Everything here runs on the UI thread; the engine does its work on the thread pool.
/// </summary>
internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly GuiLog _log = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SettingsStore _store;
    private readonly HttpMessageHandler? _handler;
    private SharedEngine? _engine;
    private CancellationTokenSource? _stop;
    private Task? _run;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainViewModel"/> class with the settings stored.
    /// </summary>
    /// <param name="paths">Where the worker keeps its data.</param>
    /// <param name="handler">The HTTP handler of the server, for tests.</param>
    public MainViewModel(WorkerPaths paths, HttpMessageHandler? handler = null)
    {
        _handler = handler;
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("System", LogLevel.Warning)
            .AddProvider(_log));
        _logger = _loggerFactory.CreateLogger("Jellyfin.Hls.Worker");
        _store = new SettingsStore(paths);
        Uploads = new UploadLogViewModel(new UploadLog(paths.Database));
        try
        {
            Settings = _store.Read(out var imported);
            if (imported)
            {
                _logger.LogInformation("已从 {Path} 导入配置", paths.LegacySettings);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status = "无法读取配置：" + ex.Message;
        }

        foreach (var connection in Settings.Connections)
        {
            AddConnectionViewModel(connection);
        }

        TargetOptions.Rebuild(Settings.Connections);
        foreach (var scheme in Settings.Schemes)
        {
            AddSchemeViewModel(scheme);
        }

        RebuildSchemeChoices();
        foreach (var library in Settings.Libraries)
        {
            Libraries.Add(new LibraryViewModel(library, SchemeChoices));
        }

        foreach (var preEncode in Settings.PreEncodes)
        {
            AddPreEncodeViewModel(preEncode);
        }

        UpdatePreEncodeQualities();
        SelectedConnection = Connections.FirstOrDefault();
        SelectedScheme = Schemes.FirstOrDefault();
        SelectedLibrary = Libraries.FirstOrDefault();
        SelectedPreEncode = PreEncodes.FirstOrDefault();
    }

    public ObservableCollection<string> Log => _log.Lines;

    public UploadLogViewModel Uploads { get; }

    public IReadOnlyList<string> Encoders { get; } = WorkerSettings.Encoders;

    public IReadOnlyList<int> PartConcurrencies { get; } = ConnectionSettings.PartConcurrencies;

    /// <summary>Gets the directory of the data of the worker.</summary>
    public string DataDirectory => _store.Paths.DataDirectory;

    /// <summary>Gets the directory of the temporary files when none is set.</summary>
    public string DefaultTempDirectory => _store.Paths.DefaultTempDirectory;

    /// <summary>Gets the settings being edited; they are written by <see cref="TrySave"/>.</summary>
    [ObservableProperty]
    public partial WorkerSettings Settings { get; private set; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets whether a field of the window holds text that is no value; set by the window.</summary>
    public Func<bool>? HasInputErrors { get; set; }

    /// <summary>Gets or sets the dialogs; set by the window. Without them, nothing that needs one is done.</summary>
    public IDialogs? Dialogs { get; set; }

    public bool IsEngineInUse => _engine is not null;

    /// <summary>Gets or sets the id of the worker as text: 32 hexadecimal digits, or empty for none yet.</summary>
    /// <exception cref="ArgumentException">The text is no id; the field shows it.</exception>
    public string WorkerIdText
    {
        get => Settings.WorkerId.Equals(Guid.Empty) ? string.Empty : Settings.WorkerId.ToString("N");
        set
        {
            var id = string.IsNullOrWhiteSpace(value) ? Guid.Empty
                : Guid.TryParse(value.Trim(), out var parsed) ? parsed
                : throw new ArgumentException("Worker Id 应为 uuid（32 位十六进制），或留空由服务器分配。");
            if (!id.Equals(Settings.WorkerId))
            {
                Settings.WorkerId = id;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WorkerIdButtonText));
            }
        }
    }

    public string WorkerIdButtonText => Settings.WorkerId.Equals(Guid.Empty) ? "申请" : "重新申请";

    // The API key a worker uses: the one of the environment, or the one set.
    private string ApiKey => Environment.GetEnvironmentVariable("JELLYFIN_HLS_API_KEY") is { Length: > 0 } key ? key : Settings.ApiKey;

    /// <summary>
    /// Writes the settings, then reports whether a worker can start with them.
    /// </summary>
    /// <returns>Whether the settings were written.</returns>
    public bool TrySave()
    {
        // A field whose text is no value keeps the value before, which is not what is shown.
        if (HasInputErrors?.Invoke() == true)
        {
            Status = "有字段的值无效（“设置”页中标红处），请先改正";
            return false;
        }

        Settings.Connections = Connections.Select(c => c.Model).ToList();
        try
        {
            _store.Write(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            // An argument exception is a value JSON has no number for, such as NaN.
            Status = "保存失败：" + ex.Message;
            return false;
        }

        var applies = IsEngineInUse ? "；上传并发已立即生效，其他设置在正在运行的 Worker 停止后再开始才使用" : string.Empty;
        try
        {
            var loaded = _store.Load();

            // The uploads of the running worker take the new limits at once; the rest waits for the next start.
            _engine?.Engine.SetUploadLimits(loaded.UploadConcurrency, loaded.UploadFileConcurrency);
            Status = "已保存" + applies;
        }
        catch (ArgumentException ex)
        {
            Status = "已保存，但配置还不能使用：" + ex.Message;
        }

        return true;
    }

    /// <summary>
    /// Reads the libraries and the pre-encoded libraries from the server once, when it is set; a failure is only logged.
    /// </summary>
    public async void RefreshInBackground()
    {
        if (string.IsNullOrWhiteSpace(Settings.Server) || string.IsNullOrWhiteSpace(ApiKey))
        {
            return;
        }

        await RefreshLibrariesAsync();
        await RefreshPreEncodesAsync();
    }

    /// <summary>
    /// Gets the engine of the saved settings: the one in use, or a new one. Each call is matched by <see cref="ReleaseEngine"/>.
    /// </summary>
    /// <returns>The engine, or <c>null</c> when the settings cannot be used, which is shown.</returns>
    public SharedEngine? AcquireEngine()
    {
        if (_engine is null)
        {
            if (LoadSettings() is not { } settings)
            {
                return null;
            }

            // Settings that pass the checks may still be refused here, such as an API key with a line break in it.
            try
            {
                var engine = new WorkerEngine(settings, _store, _loggerFactory, _handler);
                engine.WorkerIdAssigned += id => Dispatcher.UIThread.Post(() => SetWorkerId(id));
                _engine = new SharedEngine(settings, engine);
            }
            catch (Exception ex)
            {
                _logger.LogError("{Message}", ex.Message);
                Status = ex.Message;
                return null;
            }
        }

        _engine.Users++;
        return _engine;
    }

    /// <summary>Releases the engine; the last user disposes it.</summary>
    public void ReleaseEngine()
    {
        if (_engine is { } engine && --engine.Users == 0)
        {
            engine.Engine.Dispose();
            _engine = null;
        }
    }

    /// <summary>
    /// Stops the worker, the additions and the tests in progress, and waits for them, before the window closes.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task ShutdownAsync()
    {
        Stop();
        CancelAllAdds();
        await _tests.CancelAsync();
        await Task.WhenAll(_testing);
        if (_adding is { } adding)
        {
            await adding;
        }

        if (_run is { } run)
        {
            await run;
        }
    }

    public void Dispose()
    {
        _stop?.Dispose();
        _addCancel?.Dispose();
        _tests.Dispose();
        _engine?.Engine.Dispose();
        _loggerFactory.Dispose();
    }

    // A client of the server as it is set, saved or not, for what needs no worker.
    private ServerClient? CreateClient()
    {
        if (!Uri.TryCreate(Settings.Server, UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(ApiKey))
        {
            Status = "需要服务器地址和 API Key（“设置 → 全局设置”）";
            return null;
        }

        try
        {
            return new ServerClient(server, ApiKey, TimeSpan.FromSeconds(Math.Clamp(Settings.ControlTimeoutSeconds, 1, 600)), _handler);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            Status = ex.Message;
            return null;
        }
    }

    private void SetWorkerId(Guid id)
    {
        Settings.WorkerId = id;
        OnPropertyChanged(nameof(WorkerIdText));
        OnPropertyChanged(nameof(WorkerIdButtonText));
    }

    [RelayCommand]
    private void Save() => TrySave();

    // A new id forgets the media added as files of this machine with the one before: the server gives their jobs to that id.
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RequestWorkerIdAsync()
    {
        if (!Settings.WorkerId.Equals(Guid.Empty)
            && (Dialogs is not { } dialogs
                || !await dialogs.ConfirmAsync("重新申请后，之前用旧 Id 添加的 worker-local 媒体不会再下发给本机。确定？", "重新申请", "取消")))
        {
            return;
        }

        if (!TrySave() || CreateClient() is not { } client)
        {
            return;
        }

        using (client)
        {
            try
            {
                var id = await client.RegisterWorkerAsync(null, Settings.Name, CancellationToken.None);
                SetWorkerId(id);
                _store.SetWorkerId(id);
                _logger.LogInformation("服务器分配了 Worker Id {WorkerId}", id.ToString("N"));
                Status = "已申请 Worker Id";
            }
            catch (Exception ex) when (ex is ServerException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogError("申请 Worker Id 失败：{Message}", ex.Message);
                Status = "申请 Worker Id 失败：" + ex.Message;
            }
        }
    }

    // The libraries pre-encoded are offered with the schemes their jobs are run with; the others are left to other workers.
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Dialogs is not { } dialogs || !TrySave() || AcquireEngine() is not { } engine)
        {
            return;
        }

        var dialog = new AddDialogViewModel(AddDialogMode.Start, engine.Settings, engine.Engine.GetLibrariesAsync, engine.Engine.GetPreEncodedLibrariesAsync, []);
        while (true)
        {
            if (!await dialogs.ShowAddDialogAsync(dialog))
            {
                ReleaseEngine();
                return;
            }

            if (dialog.Plans.Count == 0)
            {
                Status = "没有可以领取任务的媒体库。";
                ReleaseEngine();
                return;
            }

            if (dialog.Unconfigured.Count == 0
                || await dialogs.ConfirmAsync("以下媒体库未配置，将会跳过该媒体库下的任务：\n" + string.Join("\n", dialog.Unconfigured), "继续", "返回"))
            {
                break;
            }
        }

        IsRunning = true;
        _stop = new CancellationTokenSource();
        _run = RunAsync(engine, dialog.Plans, _stop.Token);
        await _run;
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        if (_stop is { IsCancellationRequested: false } stop)
        {
            _logger.LogInformation("正在停止，进行中的任务结束后退出");
            stop.Cancel();
        }
    }

    // The saved settings as a worker uses them, or null when they cannot be used, which is shown.
    private WorkerSettings? LoadSettings()
    {
        try
        {
            return _store.Load();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError("{Message}", ex.Message);
            Status = ex.Message;
            return null;
        }
    }

    private async Task RunAsync(SharedEngine engine, IReadOnlyDictionary<Guid, HlsPlan> plans, CancellationToken cancellationToken)
    {
        Status = "运行中";
        int code;
        try
        {
            // On the thread pool: the work directory is cleaned before anything is awaited.
            code = await Task.Run(() => engine.Engine.RunAsync(plans, false, cancellationToken), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            code = WorkerEngine.Stopped;
        }
        catch (Exception ex)
        {
            _logger.LogError("{Message}", ex.Message);
            code = WorkerEngine.Failed;
        }
        finally
        {
            IsRunning = false;
            _run = null;
            ReleaseEngine();
        }

        Status = code switch
        {
            WorkerEngine.Stopped => "已停止",
            WorkerEngine.Success => "已结束",
            _ => "已停止：服务器不可用或配置有误，详见日志"
        };
    }

}

/// <summary>
/// An engine, with the settings it was made with, shared by the run and the additions.
/// </summary>
/// <param name="Settings">The settings, as loaded.</param>
/// <param name="Engine">The engine.</param>
internal sealed record SharedEngine(WorkerSettings Settings, WorkerEngine Engine)
{
    public int Users { get; set; }
}
