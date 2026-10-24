using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Media;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The additions: the list, and the queue that runs them one at a time.
/// </summary>
internal sealed partial class MainViewModel
{
    private readonly long _epoch = Stopwatch.GetTimestamp();
    private CancellationTokenSource? _addCancel;
    private Task? _adding;

    public ObservableCollection<AddTaskViewModel> AddTasks { get; } = [];

    /// <summary>Gets whether the queue is running.</summary>
    [ObservableProperty]
    public partial bool IsAdding { get; private set; }

    /// <summary>
    /// Asks what to add the files to, then puts them in the list; a queue running takes them in their turn.
    /// </summary>
    /// <param name="paths">The files and folders.</param>
    /// <returns>A task.</returns>
    public async Task OpenAddDialogAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0 || Dialogs is not { } dialogs || !TrySave() || AcquireEngine() is not { } engine)
        {
            return;
        }

        var dialog = new AddDialogViewModel(AddDialogMode.Add, engine.Settings, engine.Engine.GetLibrariesAsync, engine.Engine.GetPreEncodedLibrariesAsync, paths);
        if (!await dialogs.ShowAddDialogAsync(dialog) || dialog.Request is not { } request || dialog.SelectedLibrary is not { } library)
        {
            ReleaseEngine();
            return;
        }

        var task = new AddTaskViewModel(engine, request, library.Name);
        AddTasks.Add(task);
        Status = "已加入添加列表：" + task.Title;
        UpdateAddCommands();
        await ListFilesAsync(task);
    }

    // The files of the folders are listed off the UI thread, which a large folder or a network drive would hold up. A path that
    // cannot be listed is told when the addition runs.
    private static async Task ListFilesAsync(AddTaskViewModel task)
    {
        try
        {
            var files = await Task.Run(() => MediaAdder.Expand(task.Request.Paths, string.Empty));
            task.ShowFiles([.. files.Select(f => f.Path)]);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }
    }

    // While media is added, the worker claims no job. The additions that failed or were cancelled are queued again; those done are kept.
    [RelayCommand(CanExecute = nameof(CanStartAdds))]
    private Task StartAddsAsync()
    {
        RetryAdds();
        if (IsAdding)
        {
            UpdateAddCommands();
            return _adding ?? Task.CompletedTask;
        }

        if (!AddTasks.Any(t => t.State == AddTaskState.Waiting))
        {
            UpdateAddCommands();
            return Task.CompletedTask;
        }

        IsAdding = true;
        _addCancel = new CancellationTokenSource();
        _adding = RunAddsAsync(_addCancel.Token);
        UpdateAddCommands();
        return _adding;
    }

    private bool CanStartAdds() => AddTasks.Any(IsRetryable)
        || (!IsAdding && AddTasks.Any(t => t.State == AddTaskState.Waiting));

    // Each addition queued again takes a use of the engine of the saved settings, as one added does; when there is none, those left stay as they are.
    private void RetryAdds()
    {
        var retries = AddTasks.Where(IsRetryable).ToList();
        if (retries.Count == 0 || !TrySave())
        {
            return;
        }

        foreach (var task in retries)
        {
            if (AcquireEngine() is not { } engine)
            {
                return;
            }

            task.Retry(engine);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelAddTask))]
    private static void CancelAddTask(AddTaskViewModel? task) => task?.Cancel();

    private static bool CanCancelAddTask(AddTaskViewModel? task) => task is { IsRunning: true };

    [RelayCommand(CanExecute = nameof(CanRemoveAddTask))]
    private void RemoveAddTask(AddTaskViewModel? task)
    {
        if (task is null || task.IsRunning || !AddTasks.Remove(task))
        {
            return;
        }

        if (task.State == AddTaskState.Waiting && task.ReleaseEngineOnce())
        {
            ReleaseEngine();
        }

        UpdateAddCommands();
    }

    private static bool CanRemoveAddTask(AddTaskViewModel? task) => task is { IsRunning: false };

    [RelayCommand(CanExecute = nameof(CanCancelAllAdds))]
    private void CancelAllAdds()
    {
        _addCancel?.Cancel();
        foreach (var task in AddTasks.Where(t => t.State == AddTaskState.Waiting).ToList())
        {
            task.MarkCancelled();
            if (task.ReleaseEngineOnce())
            {
                ReleaseEngine();
            }
        }

        UpdateAddCommands();
    }

    private bool CanCancelAllAdds() => IsAdding || AddTasks.Any(t => t.State == AddTaskState.Waiting);

    [RelayCommand(CanExecute = nameof(CanClearFinishedAdds))]
    private void ClearFinishedAdds()
    {
        for (var i = AddTasks.Count - 1; i >= 0; i--)
        {
            if (AddTasks[i].IsFinished)
            {
                AddTasks.RemoveAt(i);
            }
        }

        UpdateAddCommands();
    }

    private bool CanClearFinishedAdds() => AddTasks.Any(t => t.IsFinished);

    private void UpdateAddCommands()
    {
        StartAddsCommand.NotifyCanExecuteChanged();
        CancelAllAddsCommand.NotifyCanExecuteChanged();
        ClearFinishedAddsCommand.NotifyCanExecuteChanged();
        CancelAddTaskCommand.NotifyCanExecuteChanged();
        RemoveAddTaskCommand.NotifyCanExecuteChanged();
    }

    private static bool IsRetryable(AddTaskViewModel task) => task.CanRetry && task.State is AddTaskState.Failed or AddTaskState.Cancelled;

    // The first one waiting, each in turn, so that those put in the list meanwhile are taken too. One that uses a directory deleted
    // since it was put in the list is cancelled without being run.
    private async Task RunAddsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested
                && AddTasks.FirstOrDefault(t => t.State == AddTaskState.Waiting) is { } task)
            {
                if (task.FindDeleted() is { } deleted)
                {
                    _logger.LogWarning("不执行添加 {Title}：目录 {Label} 已删除", task.Title, deleted);
                    task.Refuse("目录 " + deleted + " 已删除，不能执行，请重新添加");
                    if (task.ReleaseEngineOnce())
                    {
                        ReleaseEngine();
                    }

                    UpdateAddCommands();
                    continue;
                }

                await RunAddTaskAsync(task, cancellationToken);
            }
        }
        finally
        {
            IsAdding = false;
            _adding = null;
            _addCancel?.Dispose();
            _addCancel = null;
            UpdateAddCommands();
        }
    }

    private TimeSpan Now => Stopwatch.GetElapsedTime(_epoch);

    /// <summary>
    /// Brings the times of the steps being run up to date.
    /// </summary>
    public void TickAdds()
    {
        foreach (var task in AddTasks.Where(t => t.IsRunning))
        {
            task.Tick(Now);
        }
    }

    private async Task RunAddTaskAsync(AddTaskViewModel task, CancellationToken cancellationToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        task.Start(cancel);
        var progress = new Progress<AddEvent>(e => task.Apply(e, Now));
        UpdateAddCommands();
        Status = "正在添加：" + task.Title;
        try
        {
            // On the thread pool: a folder is listed before anything is awaited.
            var result = await Task.Run(() => task.Engine.Engine.AddAsync(task.Request, cancel.Token, progress), cancel.Token);
            task.Complete(result);
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            // Whatever it ended with, it was cancelled.
            _logger.LogWarning("添加已取消：{Title}", task.Title);
            task.MarkCancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError("添加失败：{Message}", ex.Message);
            task.Fail(ex.Message);
        }
        finally
        {
            if (task.ReleaseEngineOnce())
            {
                ReleaseEngine();
            }

            UpdateAddCommands();
        }

        Status = task.Title + "：" + task.StatusText;
    }
}
