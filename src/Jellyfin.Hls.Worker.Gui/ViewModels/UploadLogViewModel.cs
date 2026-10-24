using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Jobs;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// A file uploaded, as the page of the uploads shows it below its operation.
/// </summary>
/// <param name="Record">The record.</param>
/// <param name="Operation">How its operation is; <c>null</c> for a record written before operations.</param>
internal sealed record UploadRecordRow(UploadRecord Record, OperationState? Operation = null)
{
    public string Time => Record.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string Kind => KindOf(Record.Kind);

    // A file uploaded by an operation that did not end well is cancelled with it; the server did not take it.
    public string Result => !Record.Succeeded ? "失败"
        : Record.State == UploadState.Collected ? "成功"
        : Record.State == UploadState.Removed ? "已清理"
        : Operation is OperationState.Running ? "已上传"
        : Operation is null ? "成功"
        : "已取消";

    public bool Succeeded => Result is "成功" or "已上传";

    public bool Failed => Result == "失败";

    public string? Error => Record.Error;

    public string FileName => Path.GetFileName(Record.LocalPath);

    public string Destination => Record.Destination;

    public string Size => FormatSize(Record.Size);

    public static string KindOf(UploadKind kind) => kind switch
    {
        UploadKind.Source => "源文件",
        UploadKind.Hls => "HLS",
        UploadKind.LibraryFile => "媒体库文件",
        UploadKind.Shortcut => ".strm",
        UploadKind.TrueCache => "TrueCache",
        _ => kind.ToString()
    };

    public static string FormatSize(long size) => (size / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
}

/// <summary>
/// An operation, as the page of the uploads shows it: the addition of a file or a job, whose files are shown once it is expanded;
/// or a record written before operations, on its own.
/// </summary>
internal sealed partial class UploadGroupRow : ObservableObject
{
    private readonly OperationSummary? _summary;
    private readonly UploadRecord? _legacy;
    private readonly Func<string, IReadOnlyList<UploadRecord>>? _load;
    private Task? _loading;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadGroupRow"/> class for an operation.
    /// </summary>
    /// <param name="summary">The operation.</param>
    /// <param name="load">Reads the records of an operation.</param>
    public UploadGroupRow(OperationSummary summary, Func<string, IReadOnlyList<UploadRecord>> load)
    {
        _summary = summary;
        _load = load;
        Started = summary.Operation.Started;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadGroupRow"/> class for a record written before operations.
    /// </summary>
    /// <param name="legacy">The record.</param>
    public UploadGroupRow(UploadRecord legacy)
    {
        _legacy = legacy;
        Started = legacy.Time;
    }

    public DateTime Started { get; }

    public string Time => Started.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string Kind => _summary is { } summary
        ? summary.Operation.Kind == OperationKind.Job ? "编码任务" : "添加"
        : UploadRecordRow.KindOf(_legacy!.Kind);

    public string Status => _summary is { } summary
        ? StateOf(summary.Operation.State) + (summary.Operation.Cleaned ? "（已清理）" : string.Empty)
        : new UploadRecordRow(_legacy!).Result;

    public string? Error => _summary is { } summary ? summary.Operation.Error : _legacy!.Error;

    public bool Succeeded => _summary is { } summary ? summary.Operation.State == OperationState.Committed : _legacy!.Succeeded;

    public bool Failed => _summary is { } summary ? summary.Operation.State is OperationState.Failed or OperationState.Abandoned : !_legacy!.Succeeded;

    public string FileName => Path.GetFileName(_summary?.Operation.LocalPath ?? _legacy!.LocalPath);

    public string Target => _summary is { } summary
        ? summary.Operation.Kind == OperationKind.Job ? summary.Operation.EntryPath : "Jellyfin 媒体库/" + summary.Operation.EntryPath
        : _legacy!.Destination;

    public string Count => _summary is { } summary
        ? summary.Files + " 个文件" + (summary.Failed > 0 ? "，失败 " + summary.Failed : string.Empty)
        : string.Empty;

    public string Size => UploadRecordRow.FormatSize(_summary?.Bytes ?? _legacy!.Size);

    public bool CanExpand => _summary is { Files: > 0 };

    public ObservableCollection<UploadRecordRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string? LoadError { get; private set; }

    /// <summary>Reads the records the first time the operation is expanded; a later call waits for that read.</summary>
    /// <returns>A task.</returns>
    internal Task LoadAsync() => _summary is null ? Task.CompletedTask : _loading ??= LoadCoreAsync(_summary);

    private async Task LoadCoreAsync(OperationSummary summary)
    {
        try
        {
            var records = await Task.Run(() => _load!(summary.Operation.Id));
            foreach (var record in records)
            {
                Rows.Add(new UploadRecordRow(record, summary.Operation.State));
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            LoadError = "读取上传记录失败：" + ex.Message;
        }
    }

    private static string StateOf(OperationState state) => state switch
    {
        OperationState.Running => "进行中",
        OperationState.Committed => "完成",
        OperationState.Failed => "失败",
        OperationState.Cancelled => "已取消",
        _ => "已被接管"
    };

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = LoadAsync();
        }
    }
}

/// <summary>
/// The page of the uploads: the latest operations with their files, and the records written before operations, the latest first.
/// </summary>
internal sealed partial class UploadLogViewModel : ObservableObject
{
    /// <summary>How many operations and records are shown at most.</summary>
    public const int Limit = 1000;

    private readonly UploadLog _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadLogViewModel"/> class.
    /// </summary>
    /// <param name="log">The log of the uploads.</param>
    public UploadLogViewModel(UploadLog log)
    {
        _log = log;
    }

    public ObservableCollection<UploadGroupRow> Groups { get; } = [];

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshAsync()
    {
        try
        {
            var (operations, legacy) = await Task.Run(() => (_log.RecentOperations(Limit), _log.RecentWithoutOperation(Limit)));
            var groups = operations.Select(o => new UploadGroupRow(o, _log.ForOperation))
                .Concat(legacy.Select(r => new UploadGroupRow(r)))
                .OrderByDescending(g => g.Started)
                .ToList();
            Groups.Clear();
            foreach (var group in groups.Take(Limit))
            {
                Groups.Add(group);
            }

            Status = groups.Count > Limit ? "显示最近 " + Limit + " 项" : "共 " + groups.Count + " 项";
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Status = "读取上传记录失败：" + ex.Message;
        }
    }
}
