using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The window: the settings file being edited, the worker running its jobs, and the log.
/// Everything here runs on the UI thread; the engine does its work on the thread pool.
/// </summary>
internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly GuiLog _log = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private SharedEngine? _engine;
    private CancellationTokenSource? _stop;
    private Task? _run;
    private CancellationTokenSource? _check;
    private Task? _checking;
    private bool _opening;

    public MainViewModel(string configPath)
    {
        _loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddFilter("System", LogLevel.Warning)
            .AddProvider(_log));
        _logger = _loggerFactory.CreateLogger("Jellyfin.Hls.Worker");
        Add = new AddViewModel(this, _logger);
        Open(configPath);
    }

    public ObservableCollection<string> Log => _log.Lines;

    public AddViewModel Add { get; }

    public IReadOnlyList<string> Encoders { get; } = WorkerSettings.Encoders;

    public IReadOnlyList<ConnectionType> ConnectionTypes { get; } = Enum.GetValues<ConnectionType>();

    [ObservableProperty]
    public partial string ConfigPath { get; private set; } = string.Empty;

    /// <summary>Gets the settings as they are written in the file; <see cref="WorkerSettings.Hls"/> is never <c>null</c>.</summary>
    [ObservableProperty]
    public partial WorkerSettings Settings { get; private set; } = new() { Hls = new HlsTargetSettings() };

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    [ObservableProperty]
    public partial ConnectionViewModel? SelectedConnection { get; set; }

    /// <summary>Gets or sets the connection of the HLS root.</summary>
    public ConnectionViewModel? HlsConnection
    {
        get => Connections.FirstOrDefault(c => c.Reference == Settings.Hls!.Connection);
        set
        {
            // While the connections of another file are put in, the list clears the selection; the file's stays.
            if (_opening)
            {
                return;
            }

            Settings.Hls!.Connection = value?.Reference ?? string.Empty;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets what tells whether a field shown has text that is not a value; set by the window.</summary>
    public Func<bool>? HasInputErrors { get; set; }

    /// <summary>Gets whether an engine is in use, so changed settings apply only once it is released.</summary>
    public bool IsEngineInUse => _engine is not null;

    /// <summary>
    /// Reads a settings file to edit; a file that does not exist yet is started with the defaults.
    /// </summary>
    /// <param name="path">The file.</param>
    public void Open(string path)
    {
        // The engine in use is of the file open; another file is opened once it is released.
        if (IsEngineInUse || IsChecking)
        {
            Status = "正在运行、检查或添加：停止后再打开其他配置";
            return;
        }

        WorkerSettings settings;
        try
        {
            settings = File.Exists(path) ? WorkerSettings.Read(path) : new WorkerSettings();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = "无法读取 " + path + "：" + ex.Message;
            return;
        }

        settings.Hls ??= new HlsTargetSettings();
        _opening = true;
        try
        {
            ConfigPath = path;
            Settings = settings;
            Connections.Clear();
            foreach (var connection in settings.Connections)
            {
                Connections.Add(new ConnectionViewModel(connection, Changed));
            }

            SelectedConnection = Connections.FirstOrDefault();
        }
        finally
        {
            _opening = false;
        }

        OnPropertyChanged(nameof(HlsConnection));
        // The libraries were of the server of the file before.
        Add.OnSettingsChanged();
        Add.ClearLibraries();
        Status = File.Exists(path) ? "已打开 " + path : "新配置，保存到 " + path;
    }

    /// <summary>
    /// Writes the settings, then reports whether a worker can start with them.
    /// </summary>
    /// <returns>Whether the settings were written.</returns>
    public bool TrySave()
    {
        if (ConfigPath.Length == 0)
        {
            Status = "没有打开配置文件：请在“设置”中打开一个配置";
            return false;
        }

        // A field whose text is no value keeps the value before, which is not what is shown.
        if (HasInputErrors?.Invoke() == true)
        {
            Status = "有字段的值无效（“设置”页中标红处），请先改正";
            return false;
        }

        // The settings know a connection whose name is not taken up by the name before, which another may have now.
        if (Connections.FirstOrDefault(c => c.Reference != c.Name) is { } pending)
        {
            Status = "连接“" + pending.Name + "”的名称为空或与其它连接重名（含改名前的名称），请先改正";
            return false;
        }

        Settings.Connections = Connections.Select(c => c.Model).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            Settings.Save(ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An argument exception is a value JSON has no number for, such as NaN.
            Status = "保存失败：" + ex.Message;
            return false;
        }

        var applies = IsEngineInUse ? "；正在运行的 Worker 停止后再开始才使用新配置" : string.Empty;
        try
        {
            WorkerSettings.Load(ConfigPath);
            Status = "已保存" + applies;
        }
        catch (ArgumentException ex)
        {
            Status = "已保存，但配置还不能使用：" + ex.Message;
        }

        return true;
    }

    [RelayCommand]
    private void Save() => TrySave();

    [RelayCommand]
    private void NewConnection()
    {
        var number = Connections.Count + 1;
        while (Connections.Any(c => c.Name == "连接" + number || c.Reference == "连接" + number))
        {
            number++;
        }

        var name = "连接" + number;
        var connection = new ConnectionViewModel(new ConnectionSettings { Name = name, Type = ConnectionType.S3 }, Changed);
        Connections.Add(connection);
        SelectedConnection = connection;
        Add.OnSettingsChanged();
    }

    [RelayCommand]
    private void RemoveConnection()
    {
        if (SelectedConnection is { } connection)
        {
            // A library whose default it was goes back to the folder of the library.
            Settings.Libraries.RemoveAll(l => l.Connection == connection.Reference);
            Connections.Remove(connection);
            SelectedConnection = Connections.FirstOrDefault();
            OnPropertyChanged(nameof(HlsConnection));
            Add.OnSettingsChanged();

            // A connection that waited for the name it was known by takes it up now.
            foreach (var waiting in Connections.Where(c => c.Reference != c.Name).ToList())
            {
                waiting.TakeUpName();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!TrySave() || AcquireEngine() is not { } engine)
        {
            return;
        }

        IsRunning = true;
        _stop = new CancellationTokenSource();
        _run = RunAsync(engine, _stop.Token);
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

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        // An engine of its own, of the settings just saved: the one running may be of settings before. A check does not use
        // the work directory, which the running engine holds.
        if (!TrySave() || LoadSettings() is not { } settings)
        {
            return;
        }

        IsChecking = true;
        _check = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _checking = CheckCoreAsync(settings, _check.Token);
        await _checking;
    }

    private async Task CheckCoreAsync(WorkerSettings settings, CancellationToken cancellationToken)
    {
        WorkerEngine? engine = null;
        try
        {
            engine = new WorkerEngine(settings, _loggerFactory);
            await Preflight.CheckAsync(settings, engine, _logger, cancellationToken);
            _logger.LogInformation("检查通过");
            Status = "检查通过";
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("检查超时");
            Status = "检查超时";
        }
        catch (Exception ex)
        {
            _logger.LogError("检查失败：{Message}", ex.Message);
            Status = "检查失败：" + ex.Message;
        }
        finally
        {
            engine?.Dispose();
            IsChecking = false;
            _checking = null;
            _check?.Dispose();
            _check = null;
        }
    }

    private bool CanCheck() => !IsChecking;

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
                _engine = new SharedEngine(settings, new WorkerEngine(settings, _loggerFactory));
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

    // The saved settings as a worker uses them, or null when they cannot be used, which is shown.
    private WorkerSettings? LoadSettings()
    {
        try
        {
            var settings = WorkerSettings.Load(ConfigPath);
            return settings.Hls is null ? throw new ArgumentException("需要配置 Hls：写入 HLS 的连接和目录。") : settings;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError("{Message}", ex.Message);
            Status = ex.Message;
            return null;
        }
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
    /// Stops the worker and an addition in progress, and waits for them, before the window closes.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task ShutdownAsync()
    {
        Stop();
        _check?.Cancel();
        await Add.CancelAndWaitAsync();
        if (_checking is { } checking)
        {
            await checking;
        }

        if (_run is { } run)
        {
            await run;
        }
    }

    public void Dispose()
    {
        _stop?.Dispose();
        _check?.Dispose();
        Add.Dispose();
        _engine?.Engine.Dispose();
        _loggerFactory.Dispose();
    }

    private async Task RunAsync(SharedEngine engine, CancellationToken cancellationToken)
    {
        Status = "运行中";
        int code;
        try
        {
            // On the thread pool: the work directory is cleaned before anything is awaited.
            code = await Task.Run(() => engine.Engine.RunAsync(false, cancellationToken), CancellationToken.None);
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

    // The HLS root, the defaults of the libraries and the destination chosen follow a connection that is renamed; the
    // destinations are those of the S3 connections.
    // An empty name is that of the folder of the library, never of a connection, and a name another connection has is its
    // own: a connection whose name is either while it is retyped takes nothing over.
    private bool Changed(ConnectionViewModel connection, string old, string name)
    {
        // A retyped connection is told the name it is known by, whatever name it has now. The name another connection is
        // known by is its own too, while that one is retyped.
        var retyped = old == name;
        if (!retyped && (name.Length == 0 || Connections.Any(c => c != connection && (c.Name == name || c.Reference == name))))
        {
            return false;
        }

        var renamed = !retyped && old.Length > 0;
        if (renamed)
        {
            if (Settings.Hls!.Connection == old)
            {
                Settings.Hls.Connection = name;
            }

            foreach (var library in Settings.Libraries.Where(l => l.Connection == old))
            {
                library.Connection = name;
            }
        }

        // A library whose default is no longer an S3 connection goes back to the folder of the library.
        if (name.Length > 0 && !connection.IsS3)
        {
            Settings.Libraries.RemoveAll(l => l.Connection == name);
        }

        OnPropertyChanged(nameof(HlsConnection));
        Add.OnSettingsChanged(renamed ? old : null, name);

        // A connection that waited for the name given up takes it up now.
        if (renamed && Connections.FirstOrDefault(c => c != connection && c.Name == old) is { } waiting)
        {
            waiting.TakeUpName();
        }

        return true;
    }
}

/// <summary>
/// An engine, with the settings it was made with, shared by the run, a check and an addition.
/// </summary>
/// <param name="Settings">The settings, as loaded.</param>
/// <param name="Engine">The engine.</param>
internal sealed record SharedEngine(WorkerSettings Settings, WorkerEngine Engine)
{
    public int Users { get; set; }
}
