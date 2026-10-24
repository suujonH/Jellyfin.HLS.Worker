using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Hls.Worker.Media;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// A file of an addition: its step, how far the step is, and how long it took and may still take.
/// </summary>
internal sealed partial class AddFileViewModel : ObservableObject
{
    private TimeSpan _started;
    private long _done;
    private long _total;
    private ProgressUnit _unit;

    public AddFileViewModel(string path)
    {
        Path = path;
    }

    /// <summary>Gets the path of the file in the library, with forward slashes.</summary>
    public string Path { get; }

    [ObservableProperty]
    public partial AddTaskState State { get; private set; }

    /// <summary>Gets the note of a file added, or the error of one that failed.</summary>
    public string? Message { get; private set; }

    [ObservableProperty]
    public partial string StepText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasProgress { get; private set; }

    /// <summary>Gets how far the step is, from 0 to 100.</summary>
    [ObservableProperty]
    public partial double Percent { get; private set; }

    [ObservableProperty]
    public partial string DetailText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TimeText { get; private set; } = string.Empty;

    public bool IsRunning => State == AddTaskState.Running;

    public bool IsFinished => State is AddTaskState.Done or AddTaskState.Failed or AddTaskState.Cancelled;

    public bool Succeeded => State == AddTaskState.Done && Message is null;

    public bool HasFailed => State == AddTaskState.Failed;

    public bool HasNote => State == AddTaskState.Done && Message is not null;

    public string StatusText => State switch
    {
        AddTaskState.Waiting => "等待",
        AddTaskState.Running => StepText,
        AddTaskState.Done => Message is null ? "完成" : "完成（" + Message + "）",
        AddTaskState.Failed => "失败：" + Message,
        _ => "已取消"
    };

    /// <summary>Formats a size: B, KB, MB or GB.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The size.</returns>
    internal static string Size(double bytes) => bytes switch
    {
        < 1024 => bytes.ToString("0", CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
        < 1024 * 1024 * 1024 => (bytes / (1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
    };

    /// <summary>Formats a time taken: mm:ss, or h:mm:ss from an hour on; down to the second.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The time.</returns>
    internal static string Duration(TimeSpan time)
    {
        var seconds = (long)Math.Max(0, Math.Floor(time.TotalSeconds));
        return seconds < 3600
            ? (seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture)
            : Clock(seconds * 1000);
    }

    /// <summary>Formats a position in the media: h:mm:ss.</summary>
    /// <param name="milliseconds">The milliseconds.</param>
    /// <returns>The position.</returns>
    internal static string Clock(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds / 1000);
        return (seconds / 3600).ToString(CultureInfo.InvariantCulture)
            + ":" + (seconds / 60 % 60).ToString("00", CultureInfo.InvariantCulture)
            + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    internal void StartStep(AddStep step, string detail, TimeSpan now)
    {
        if (IsFinished)
        {
            return;
        }

        State = AddTaskState.Running;
        _started = now;
        _done = _total = 0;
        StepText = Label(step, detail);
        HasProgress = false;
        Percent = 0;
        Refresh(now);
    }

    internal void Advance(long done, long total, ProgressUnit unit, TimeSpan now)
    {
        if (State != AddTaskState.Running)
        {
            return;
        }

        (_done, _total, _unit) = (done, total, unit);
        HasProgress = total > 0;
        Percent = total > 0 ? Math.Clamp(done * 100.0 / total, 0, 100) : 0;
        Refresh(now);
    }

    internal void End(bool added, string? message)
    {
        if (IsFinished)
        {
            return;
        }

        Settle(added ? AddTaskState.Done : AddTaskState.Failed, message);
    }

    internal void MarkFailed(string message)
    {
        if (!IsFinished)
        {
            Settle(AddTaskState.Failed, message);
        }
    }

    internal void MarkCancelled()
    {
        if (!IsFinished)
        {
            Settle(AddTaskState.Cancelled, null);
        }
    }

    internal void Tick(TimeSpan now)
    {
        if (State == AddTaskState.Running)
        {
            Refresh(now);
        }
    }

    private static string Label(AddStep step, string detail) => step switch
    {
        AddStep.Hash => "计算校验值",
        AddStep.Entry => "生成 TrueCache 条目",
        AddStep.Upload => "上传到 " + detail,
        AddStep.Library => "写入媒体库",
        AddStep.Shortcut => "写入 .strm",
        AddStep.Probe => "申请编码",
        AddStep.EncodeVideo => "编码视频 " + detail,
        AddStep.EncodeAudio => "编码音频（" + detail + "）",
        AddStep.Subtitles => "提取字幕（" + detail + "）",
        AddStep.Preview => "生成预览图",
        AddStep.Prepare => "准备（占用、续传与清理）",
        _ => "上传 HLS 到 " + detail
    };

    private void Settle(AddTaskState state, string? message)
    {
        Message = message;
        HasProgress = false;
        DetailText = string.Empty;
        TimeText = string.Empty;
        State = state;
    }

    private void Refresh(TimeSpan now)
    {
        var elapsed = now - _started;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var part = HasProgress ? (double)_done / _total : 0;
        if (HasProgress)
        {
            var percent = (part * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            DetailText = _unit switch
            {
                ProgressUnit.Bytes => percent + " · " + Size(_done) + " / " + Size(_total)
                    + (elapsed.TotalSeconds >= 1 ? " · " + Size(_done / elapsed.TotalSeconds) + "/s" : string.Empty),
                ProgressUnit.Milliseconds => percent + " · " + Clock(_done) + " / " + Clock(_total)
                    + (elapsed.TotalSeconds >= 1 ? " · " + (_done / elapsed.TotalMilliseconds).ToString("0.0", CultureInfo.InvariantCulture) + "x" : string.Empty),
                _ => percent + " · " + _done + " / " + _total + " 个文件"
            };
        }
        else
        {
            DetailText = string.Empty;
        }

        TimeText = "已用 " + Duration(elapsed) + (!HasProgress ? string.Empty
            : part >= 0.01 && elapsed.TotalSeconds >= 3 ? " · 剩余约 " + Duration(elapsed * ((1 - part) / part))
            : " · 剩余 --:--");
    }

    partial void OnStateChanged(AddTaskState value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(Succeeded));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(HasNote));
    }

    partial void OnStepTextChanged(string value) => OnPropertyChanged(nameof(StatusText));
}
