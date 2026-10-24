using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Media;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>Where an addition is.</summary>
internal enum AddTaskState
{
    Waiting,
    Running,
    Done,
    Failed,
    Cancelled
}

/// <summary>
/// An addition in the list: what to add, and how it went. It holds a use of the engine from when it is queued until it ends or is removed;
/// one that failed or was cancelled can be queued again with a new use.
/// </summary>
internal sealed partial class AddTaskViewModel : ObservableObject
{
    private CancellationTokenSource? _cancel;
    private bool _released;

    public AddTaskViewModel(SharedEngine engine, AddRequest request, string libraryName)
    {
        Engine = engine;
        Request = request;
        LibraryName = libraryName;
        Title = Name(request.Paths[0]) + (request.Paths.Count > 1 ? " 等 " + request.Paths.Count + " 项" : string.Empty);
        PathsTip = string.Join(Environment.NewLine, request.Paths);
    }

    public SharedEngine Engine { get; private set; }

    public AddRequest Request { get; }

    public string LibraryName { get; }

    public string Title { get; }

    public string PathsTip { get; }

    [ObservableProperty]
    public partial AddTaskState State { get; private set; }

    public AddResult? Result { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Gets whether the addition can be queued again: not when a directory it uses was deleted.</summary>
    public bool CanRetry { get; private set; } = true;

    /// <summary>Gets the files, once the addition listed them.</summary>
    public ObservableCollection<AddFileViewModel> Files { get; } = [];

    /// <summary>Gets the file being added.</summary>
    [ObservableProperty]
    public partial AddFileViewModel? Current { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Gets whether the files can be shown, there being more than one.</summary>
    public bool HasFiles => Files.Count > 1;

    /// <summary>Gets whether the file being added is shown below the addition, the files not being.</summary>
    public bool ShowCurrent => Current is not null && !IsExpanded;

    public bool IsRunning => State == AddTaskState.Running;

    public bool IsFinished => State is AddTaskState.Done or AddTaskState.Failed or AddTaskState.Cancelled;

    public bool Succeeded => State == AddTaskState.Done && Result is { Failed: 0 };

    public bool HasFailed => State == AddTaskState.Failed || (State == AddTaskState.Done && Result is { Failed: > 0 });

    public string StatusText => State switch
    {
        AddTaskState.Waiting => "等待",
        AddTaskState.Running => Files.Count > 1 ? "进行中 " + Files.Count(f => f.IsFinished) + "/" + Files.Count : "进行中",
        AddTaskState.Cancelled => Error is null ? "已取消" : "已取消：" + Error,
        AddTaskState.Failed => "失败：" + Error,
        _ => "完成：成功 " + Result!.Added + (Result.Failed > 0 ? "，失败 " + Result.Failed : string.Empty)
    };

    internal void Start(CancellationTokenSource cancel)
    {
        if (State != AddTaskState.Waiting)
        {
            throw new InvalidOperationException();
        }

        _cancel = cancel;
        State = AddTaskState.Running;
    }

    internal void Complete(AddResult result)
    {
        _cancel = null;
        Result = result;
        Settle(f => f.MarkFailed("未完成"));
        State = AddTaskState.Done;
    }

    internal void Fail(string message)
    {
        _cancel = null;
        Error = message;
        Settle(f => f.MarkFailed(message));
        State = AddTaskState.Failed;
    }

    internal void MarkCancelled()
    {
        _cancel = null;
        Settle(f => f.MarkCancelled());
        State = AddTaskState.Cancelled;
    }

    /// <summary>Queues again an addition that failed or was cancelled, from the start: every file is added again.</summary>
    /// <param name="engine">The use of the engine it holds until it ends.</param>
    internal void Retry(SharedEngine engine)
    {
        if (State is not (AddTaskState.Failed or AddTaskState.Cancelled))
        {
            throw new InvalidOperationException();
        }

        Engine = engine;
        _released = false;
        Result = null;
        Error = null;
        Current = null;
        OnPropertyChanged(nameof(Error));
        SetFiles([.. Files.Select(f => f.Path)]);
        State = AddTaskState.Waiting;
    }

    /// <summary>Shows the files of an addition waiting, as listed when it was put in the list; it lists them again when it runs.</summary>
    /// <param name="files">The paths of the files in the library, in the order they are added.</param>
    internal void ShowFiles(IReadOnlyList<string> files)
    {
        if (State == AddTaskState.Waiting)
        {
            SetFiles(files);
        }
    }

    /// <summary>Finds a directory the addition uses that the settings of its engine no longer have.</summary>
    /// <returns>Its label, or <c>null</c> when they have every one.</returns>
    internal string? FindDeleted()
    {
        var ids = Engine.Settings.Connections.Select(c => c.Id).ToHashSet();
        return Find(c => !ids.Contains(c.Id));
    }

    /// <summary>
    /// Finds a directory the addition uses whose place the settings of its engine changed since it was put in the list, such as its
    /// root or bucket: what it declares was planned for the old place.
    /// </summary>
    /// <returns>Its label, or <c>null</c> when every one is where it was.</returns>
    internal string? FindMoved()
    {
        var connections = Engine.Settings.Connections.ToDictionary(c => c.Id);
        return Find(c => connections.TryGetValue(c.Id, out var current) && !current.IsSamePlace(c));
    }

    private string? Find(Func<ConnectionSettings, bool> match)
    {
        bool Matches(ConnectionSettings? connection) => connection is not null && match(connection);
        if (Request.Source.Uploads.Concat(Request.Hls?.Uploads ?? []).FirstOrDefault(t => Matches(t.Connection)) is { } target)
        {
            return target.Label;
        }

        return Matches(Request.Source.Declaration?.Connection) ? Request.Source.Declaration!.Connection!.Name
            : Matches(Request.Hls?.DeclaredS3) ? Request.Hls!.DeclaredS3!.Name
            : null;
    }

    /// <summary>Cancels a waiting addition that cannot be run, for good: it cannot be queued again.</summary>
    /// <param name="reason">Why.</param>
    internal void Refuse(string reason)
    {
        if (State != AddTaskState.Waiting)
        {
            throw new InvalidOperationException();
        }

        _cancel = null;
        Error = reason;
        CanRetry = false;
        OnPropertyChanged(nameof(Error));
        State = AddTaskState.Cancelled;
    }

    /// <summary>Applies what the addition told; only while it runs.</summary>
    /// <param name="e">What happened.</param>
    /// <param name="now">The time, for the time a step takes.</param>
    internal void Apply(AddEvent e, TimeSpan now)
    {
        if (State != AddTaskState.Running)
        {
            return;
        }

        switch (e)
        {
            case AddFilesListed listed:
                SetFiles(listed.Files);
                break;
            case AddStepStarted started when IsFile(started.File):
                Files[started.File].StartStep(started.Step, started.Detail, now);
                Current = Files[started.File];
                break;
            case AddStepAdvanced advanced when IsFile(advanced.File):
                Files[advanced.File].Advance(advanced.Done, advanced.Total, advanced.Unit, now);
                break;
            case AddFileEnded ended when IsFile(ended.File):
                Files[ended.File].End(ended.Added, ended.Message);
                if (Current == Files[ended.File])
                {
                    Current = null;
                }

                OnPropertyChanged(nameof(StatusText));
                break;
        }
    }

    internal void Tick(TimeSpan now) => Current?.Tick(now);

    // The state is set by the queue once the addition has ended.
    internal void Cancel() => _cancel?.Cancel();

    /// <summary>Tells whether the use of the engine is still to be released, once.</summary>
    /// <returns><c>true</c> the first time.</returns>
    internal bool ReleaseEngineOnce()
    {
        if (_released)
        {
            return false;
        }

        _released = true;
        return true;
    }

    private bool IsFile(int index) => index >= 0 && index < Files.Count;

    private void SetFiles(IReadOnlyList<string> files)
    {
        Files.Clear();
        foreach (var path in files)
        {
            Files.Add(new AddFileViewModel(path));
        }

        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(StatusText));
    }

    // The files not ended when the addition did.
    private void Settle(Action<AddFileViewModel> settle)
    {
        foreach (var file in Files)
        {
            settle(file);
        }

        Current = null;
    }

    // A folder by its name; a root such as D:\ as it is.
    private static string Name(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return name.Length == 0 ? path : name;
    }

    partial void OnStateChanged(AddTaskState value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(Succeeded));
        OnPropertyChanged(nameof(HasFailed));
    }

    partial void OnCurrentChanged(AddFileViewModel? value) => OnPropertyChanged(nameof(ShowCurrent));

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ShowCurrent));
}
