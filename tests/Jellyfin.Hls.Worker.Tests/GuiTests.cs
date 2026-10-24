using System.Net;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Gui.ViewModels;
using Jellyfin.Hls.Worker.Jobs;
using Jellyfin.Hls.Worker.Media;
using Jellyfin.Hls.Worker.Protocol;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class GuiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-gui-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _s3 = Guid.NewGuid();
    private readonly Guid _backup = Guid.NewGuid();
    private readonly Guid _scheme = Guid.NewGuid();
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _worker = Guid.NewGuid();
    private readonly WorkerPaths _paths;

    public GuiTests()
    {
        _paths = new WorkerPaths(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task Uploads_AreShownByOperation_AndTheFilesOfOneThatFailedAreCancelled()
    {
        Directory.CreateDirectory(_directory);
        var log = new UploadLog(_paths.Database);
        var operation = new UploadOperation(
            Guid.NewGuid().ToString("N"), OperationKind.Add, DateTime.UtcNow, null, OperationState.Running, null, _library, "/m", "Film.strm", "C:/Film.mkv", 5, "sha", null, null, null, false);
        log.StartOperation(operation);
        log.Add(
        [
            new UploadRecord(DateTime.UtcNow, UploadKind.Source, _library, "C:/Film.mkv", "b2/Film.mkv", 5, true, null, operation.Id, _s3, "Film.mkv"),
            new UploadRecord(DateTime.UtcNow, UploadKind.Shortcut, _library, "C:/Film.mkv", "Jellyfin 媒体库/Film.strm", 9, false, "refused", operation.Id)
        ]);
        log.EndOperation(operation.Id, OperationState.Failed, "refused");
        log.Add([new UploadRecord(DateTime.UtcNow.AddDays(-1), UploadKind.Hls, null, "old", "local/old", 1, true, null)]);
        var page = new UploadLogViewModel(log);

        await page.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("共 2 项", page.Status);
        var group = page.Groups[0];
        Assert.Equal(("添加", "失败", "2 个文件，失败 1", "Jellyfin 媒体库/Film.strm", "Film.mkv", true), (group.Kind, group.Status, group.Count, group.Target, group.FileName, group.CanExpand));
        Assert.Empty(group.Rows);
        group.IsExpanded = true;
        await group.LoadAsync();
        Assert.Equal(["已取消", "失败"], group.Rows.Select(r => r.Result));
        var legacy = page.Groups[1];
        Assert.Equal(("HLS", "成功", string.Empty, false), (legacy.Kind, legacy.Status, legacy.Count, legacy.CanExpand));
    }

    [Fact]
    public void CloningAConnection_GivesTheCopyAnIdAndAFreeName()
    {
        using var main = Open();
        main.SelectedConnection = main.Connections.Single(c => c.Name == "s3");

        main.CloneConnectionCommand.Execute(null);
        main.CloneConnectionCommand.Execute(null);
        main.NewConnectionCommand.Execute(ConnectionType.Sftp);

        Assert.Equal(["s3", "backup", "s3 副本", "s3 副本 副本", "连接5"], main.Connections.Select(c => c.Name));
        Assert.Equal(ConnectionType.S3, main.Connections[3].Type);
        Assert.Equal(ConnectionType.Sftp, main.Connections[4].Type);
        Assert.Equal(5, main.Connections.Select(c => c.Model.Id).Distinct().Count());
        Assert.Equal("media", main.Connections[2].Model.Bucket);
    }

    [Fact]
    public void RemovingAConnection_LeavesTheLinesThatUsedItWithoutADirectory()
    {
        using var main = Open();
        main.SelectedConnection = main.Connections.Single(c => c.Name == "s3");

        main.RemoveConnectionCommand.Execute(null);

        var scheme = main.Schemes.Single();
        Assert.Null(scheme.Model.Hls[0].ConnectionId);
        Assert.Null(scheme.Model.Sources[1].ConnectionId);
        Assert.Equal(_backup, scheme.Model.Sources[0].ConnectionId);
        Assert.Equal(_backup, scheme.Model.Hls[1].ConnectionId);
        Assert.Equal("请选择目录。", scheme.Hls[0].Error);
        Assert.Null(scheme.Hls[1].Error);

        // Saved, but not usable until the lines are mended.
        Assert.True(main.TrySave());
        Assert.Contains("没有选择目录", Assert.Throws<ArgumentException>(new SettingsStore(_paths).Load).Message, StringComparison.Ordinal);
        scheme.RemoveEntryCommand.Execute(scheme.Hls[0]);
        scheme.RemoveEntryCommand.Execute(scheme.Sources[1]);
        Assert.True(main.TrySave());
        new SettingsStore(_paths).Load();
    }

    [Fact]
    public void Save_WritesTheSettingsToTheDatabase()
    {
        using (var main = Open())
        {
            main.Connections.Single(c => c.Name == "backup").Name = "archive";
            main.Settings.Concurrency = 3;

            Assert.True(main.TrySave());
        }

        var settings = new SettingsStore(_paths).Load();
        Assert.Equal(["s3", "archive"], settings.Connections.Select(c => c.Name));
        Assert.Equal(3, settings.Concurrency);

        using var again = new MainViewModel(_paths);
        Assert.Equal(["s3", "archive"], again.Connections.Select(c => c.Name));
    }

    [Fact]
    public void Save_WhileTheEngineIsInUse_GivesItTheUploadLimits()
    {
        using var main = Open();
        var engine = main.AcquireEngine()!;
        main.Settings.UploadConcurrency = 5;
        main.Settings.UploadFileConcurrency = 12;

        Assert.True(main.TrySave());

        Assert.Equal((5, 12), (engine.Settings.UploadConcurrency, engine.Settings.UploadFileConcurrency));
        Assert.Contains("上传并发已立即生效", main.Status, StringComparison.Ordinal);
        main.ReleaseEngine();
    }

    [Fact]
    public void Opening_ImportsTheSettingsFileOfFormerVersions()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_paths.LegacySettings, """{ "ApiKey": "k", "Connections": [ { "Name": "nas", "Type": "Local", "LocalRoot": "nas" } ], "Hls": { "Connection": "nas" } }""");

        using var main = new MainViewModel(_paths);

        Assert.Equal(ConnectionType.Worker, main.Connections.Single().Model.Type);
        Assert.Equal("导入的方案", main.Settings.Schemes.Single().Name);
    }

    [Fact]
    public void Schemes_AreAddedRenamedAndRemoved_AndARemovedOneIsNoLongerTheDefaultOfALibrary()
    {
        using var main = Open();
        var first = main.Schemes.Single();

        main.NewSchemeCommand.Execute(null);
        var second = main.SelectedScheme!;
        Assert.Equal("方案2", second.Name);
        Assert.Equal(["(无)", "a", "方案2"], main.SchemeChoices.Items.Select(o => o.Name));

        // A name may be taken: the schemes are told apart by their ids.
        second.Name = "a";
        Assert.Equal(["(无)", "a", "a"], main.SchemeChoices.Items.Select(o => o.Name));
        Assert.True(main.TrySave());
        Assert.Equal(2, new SettingsStore(_paths).Load().Schemes.Count);
        second.Name = "b";
        Assert.Equal(["(无)", "a", "b"], main.SchemeChoices.Items.Select(o => o.Name));

        Assert.Equal("a", main.Libraries.Single().Scheme!.Name);
        main.SelectedScheme = first;
        main.RemoveSchemeCommand.Execute(null);

        Assert.Equal(["b"], main.Settings.Schemes.Select(s => s.Name));
        Assert.Null(main.Settings.Libraries.Single().SchemeId);
        Assert.Equal("(无)", main.Libraries.Single().Scheme!.Name);
        Assert.True(main.TrySave());
        Assert.Null(new SettingsStore(_paths).Load().Libraries.Single().SchemeId);
    }

    [Fact]
    public void ADirectoryOfTheServer_IsOnlyDeclared()
    {
        using var main = Open();
        var scheme = main.Schemes.Single();

        Assert.Equal(["s3", "backup"], scheme.Hls[0].TargetOptions.Select(o => o.Label));
        Assert.Equal(["Jellyfin 媒体库", "s3", "backup"], scheme.Sources[0].TargetOptions.Select(o => o.Label));

        main.NewConnectionCommand.Execute(ConnectionType.Server);
        main.SelectedConnection!.Name = "server";

        Assert.Equal(["s3", "backup", "server"], scheme.Hls[0].TargetOptions.Select(o => o.Label));
        Assert.Equal(["Jellyfin 媒体库", "s3", "backup", "server"], scheme.Sources[0].TargetOptions.Select(o => o.Label));

        // It is not uploaded to: a line of it has to be declared.
        scheme.AddSourceCommand.Execute(null);
        var server = scheme.Sources[2];
        server.SelectedTarget = server.TargetOptions.Single(o => o.Label == "server");
        Assert.True(server.IsServer);
        Assert.NotNull(server.Error);
        server.Declare = true;
        Assert.Null(server.Error);

        // Declared for HLS, the renditions need another line to reach it.
        scheme.Hls[0].SelectedTarget = scheme.Hls[0].TargetOptions.Single(o => o.Label == "server");
        Assert.Null(scheme.Error);
        scheme.RemoveEntryCommand.Execute(scheme.Hls[1]);
        Assert.NotNull(scheme.Error);
    }

    [Fact]
    public void TheLinesOfAScheme_DeclareOneAtMost_AndTellWhatIsWrong()
    {
        using var main = Open();
        var scheme = main.Schemes.Single();
        Assert.Equal([true, false], scheme.Hls.Select(e => e.Declare));
        Assert.Equal([false, true], scheme.Sources.Select(e => e.Declare));
        Assert.All(scheme.Hls.Concat(scheme.Sources), e => Assert.True(e.CanDeclare));

        // Declaring one line undeclares the other; none declared is allowed.
        scheme.Hls[1].Declare = true;
        Assert.Equal([false, true], scheme.Model.Hls.Select(e => e.Declare));
        scheme.Hls[1].Declare = false;
        Assert.All(scheme.Model.Hls, e => Assert.False(e.Declare));
        Assert.Null(scheme.Error);

        // A new line has no directory yet.
        scheme.AddSourceCommand.Execute(null);
        var library = scheme.Sources[2];
        Assert.Null(library.SelectedTarget);
        Assert.Equal("请选择目录。", library.Error);

        // The library is always declared, and no other line is.
        library.SelectedTarget = TargetOptions.JellyfinLibrary;
        Assert.Null(library.Error);
        Assert.Equal([false, false, true], scheme.Model.Sources.Select(e => e.Declare));
        Assert.All(scheme.Sources, e => Assert.False(e.CanDeclare));
        library.Declare = false;
        scheme.Sources[0].Declare = true;
        Assert.Equal([false, false, true], scheme.Model.Sources.Select(e => e.Declare));

        // A second library line.
        scheme.AddSourceCommand.Execute(null);
        scheme.Sources[3].SelectedTarget = TargetOptions.JellyfinLibrary;
        Assert.NotNull(scheme.Sources[3].Error);
        scheme.RemoveEntryCommand.Execute(scheme.Sources[3]);

        scheme.RemoveEntryCommand.Execute(library);
        Assert.All(scheme.Sources, e => Assert.True(e.CanDeclare));
        Assert.True(main.TrySave());
        new SettingsStore(_paths).Load();
    }

    [Fact]
    public void Schemes_AreCloned()
    {
        using var main = Open();
        var source = main.Schemes.Single();

        main.CloneSchemeCommand.Execute(null);
        main.SelectedScheme = source;
        main.CloneSchemeCommand.Execute(null);

        Assert.Equal(["a", "a 副本", "a 副本2"], main.Schemes.Select(s => s.Name));
        Assert.Equal(3, main.Schemes.Select(s => s.Model.Id).Distinct().Count());
        Assert.Same(main.Schemes[2], main.SelectedScheme);
        var clone = main.Schemes[1].Model;
        Assert.Equal(source.Model.Hls.Select(e => (e.ConnectionId, e.Declare)), clone.Hls.Select(e => (e.ConnectionId, e.Declare)));
        Assert.Equal(source.Model.Sources.Select(e => (e.ConnectionId, e.Declare)), clone.Sources.Select(e => (e.ConnectionId, e.Declare)));
        Assert.NotSame(source.Model.Hls[0], clone.Hls[0]);
        Assert.Equal(["(无)", "a", "a 副本", "a 副本2"], main.SchemeChoices.Items.Select(c => c.Name));
        Assert.Equal(_scheme, main.Settings.Libraries.Single().SchemeId);

        Assert.True(main.TrySave());
        Assert.Equal(3, new SettingsStore(_paths).Load().Schemes.Count);
    }

    [Fact]
    public async Task Refreshing_FollowsTheLibrariesOfTheServer()
    {
        var found = Guid.NewGuid();
        var server = new FakeServer((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/Library/VirtualFolders" => FakeServer.Json(new[] { new VirtualFolder("Movies", found.ToString(), ["/srv/a", "/srv/b"]) }),
            "/HlsWorker/Libraries" => FakeServer.Json(new[] { new WorkerLibrary(found, "Movies", [1080, 720]) }),
            _ => FakeServer.Status(HttpStatusCode.NotFound)
        }));
        using var main = Open(server);

        await main.RefreshLibrariesCommand.ExecuteAsync(null);
        await main.RefreshPreEncodesCommand.ExecuteAsync(null);

        Assert.Equal(["Movies", "Old"], main.Libraries.Select(l => l.Name));
        Assert.False(main.Libraries[0].IsMissing);
        Assert.True(main.Libraries[1].IsMissing);
        Assert.True(main.Libraries[0].HasSeveralLocations);
        Assert.Equal("/srv/a", main.Libraries[0].Location);
        Assert.True(main.Libraries[0].CanPreEncode);
        Assert.False(main.Libraries[1].CanPreEncode);
        var preEncode = main.PreEncodes.Single();
        Assert.True(preEncode.Enabled);
        Assert.Equal(["1080p", "720p"], preEncode.Qualities.Select(q => q.Label));
        Assert.All(preEncode.Qualities, q => Assert.True(q.IsChecked));
        Assert.Equal([1080, 720], main.Libraries[0].PreEncodeQualities.Select(q => q.Quality));

        // A quality turned off is no longer offered; one chosen for the library and turned off comes back with it.
        main.Libraries[0].PreEncodeQualities[1].IsChecked = true;
        preEncode.Qualities[1].IsChecked = false;
        Assert.Equal([1080], main.Libraries[0].PreEncodeQualities.Select(q => q.Quality));
        main.Libraries[0].PreEncodeQualities[0].IsChecked = true;
        Assert.True(main.TrySave());
        var settings = new SettingsStore(_paths).Load();
        Assert.Equal([720], settings.PreEncodes.Single().DisabledQualities);
        Assert.Equal([720, 1080], settings.Libraries.Single(l => l.Id == found).PreEncodeQualities);
        preEncode.Qualities[1].IsChecked = true;
        Assert.Equal([true, true], main.Libraries[0].PreEncodeQualities.Select(q => q.IsChecked));

        // Every quality turned off: nothing to encode in at once.
        preEncode.Qualities[0].IsChecked = false;
        preEncode.Qualities[1].IsChecked = false;
        Assert.False(main.Libraries[0].CanPreEncode);

        Assert.Equal(2, settings.Libraries.Count);
        Assert.Equal(["/srv/a", "/srv/b"], settings.Libraries.Single(l => l.Id == found).Locations);
        Assert.True(settings.PreEncodes.Single().Enabled);
    }

    [Fact]
    public async Task TheAddDialog_FollowsTheDefaultsOfTheLibrary_ForWhatTheUserHasNotChosen()
    {
        var other = Guid.NewGuid();
        using (Open())
        {
        }

        var settings = new SettingsStore(_paths).Load();
        settings.Libraries.Single().PreEncodeQualities = [720];
        settings.Libraries.Add(new LibrarySettings { Id = other });
        settings.PreEncodes.Add(new PreEncodeSettings { LibraryId = _library, Qualities = [1080, 720, 480], DisabledQualities = [480] });
        var dialog = new AddDialogViewModel(
            AddDialogMode.Add,
            settings,
            _ => Task.FromResult<IReadOnlyList<VirtualFolder>>([new VirtualFolder("Old", _library.ToString(), ["/a"]), new VirtualFolder("Other", other.ToString(), ["/b", "/c"])]),
            _ => Task.FromResult<IReadOnlyList<WorkerLibrary>>([new WorkerLibrary(_library, "Old", [1080, 720, 480])]),
            ["movie.mkv"]);

        await dialog.LoadAsync();
        Assert.Equal(_library, dialog.SelectedLibrary!.Id);
        Assert.Equal(_scheme, dialog.SourceScheme.Id);
        Assert.Equal(_scheme, dialog.HlsScheme.Id);
        Assert.Equal([1080, 720], dialog.PreEncodeQualities.Select(q => q.Quality));
        Assert.Equal([720], dialog.PreEncodeQualities.Where(q => q.IsChecked).Select(q => q.Quality));
        Assert.False(dialog.ShowLocations);
        dialog.PreEncodeQualities[0].IsChecked = true;

        // The scheme of the sources is chosen; the others follow the library chosen next.
        dialog.SourceScheme = SchemeOption.None;
        dialog.SelectedLibrary = dialog.Libraries[1];
        Assert.Null(dialog.SourceScheme.Id);
        Assert.Null(dialog.HlsScheme.Id);
        Assert.Empty(dialog.PreEncodeQualities);
        Assert.False(dialog.CanPreEncode);
        Assert.True(dialog.ShowLocations);
        Assert.Equal("/b", dialog.SelectedLocation);

        // The qualities to encode in at once are always the library's.
        dialog.SelectedLibrary = dialog.Libraries[0];
        Assert.Null(dialog.SourceScheme.Id);
        Assert.Equal(_scheme, dialog.HlsScheme.Id);
        Assert.Equal([720], dialog.PreEncodeQualities.Where(q => q.IsChecked).Select(q => q.Quality));

        Assert.True(dialog.TryAccept());
        var request = dialog.Request!;
        Assert.Equal(_library, request.LibraryId);
        Assert.Empty(request.Source.Uploads);
        Assert.Null(request.Source.Declaration);
        Assert.NotNull(request.Hls);
        Assert.Equal([720], request.PreEncodeQualities!);
    }

    [Fact]
    public async Task TheStartDialog_TellsTheLibrariesWithoutAnHlsScheme()
    {
        var other = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        using (Open())
        {
        }

        var settings = new SettingsStore(_paths).Load();
        var turnedOff = Guid.NewGuid();
        settings.PreEncodes.Add(new PreEncodeSettings { LibraryId = disabled, Enabled = false });
        settings.PreEncodes.Add(new PreEncodeSettings { LibraryId = turnedOff, Qualities = [720], DisabledQualities = [720] });
        var dialog = new AddDialogViewModel(
            AddDialogMode.Start,
            settings,
            _ => Task.FromResult<IReadOnlyList<VirtualFolder>>([]),
            _ => Task.FromResult<IReadOnlyList<WorkerLibrary>>(
                [new WorkerLibrary(_library, "Old", [720]), new WorkerLibrary(other, "Other", [720]), new WorkerLibrary(disabled, "Off", [720]), new WorkerLibrary(turnedOff, "No quality", [720])]),
            []);

        await dialog.LoadAsync();
        Assert.Equal(["Old", "Other"], dialog.Libraries.Select(l => l.Name));

        // A choice made for one library stays with it.
        dialog.SelectedLibrary = dialog.Libraries[1];
        Assert.Null(dialog.HlsScheme.Id);
        dialog.SelectedLibrary = dialog.Libraries[0];
        Assert.Equal(_scheme, dialog.HlsScheme.Id);
        dialog.HlsScheme = SchemeOption.None;
        dialog.SelectedLibrary = dialog.Libraries[1];
        dialog.HlsScheme = dialog.SchemeOptions.Single(o => o.Id == _scheme);
        dialog.SelectedLibrary = dialog.Libraries[0];
        Assert.Null(dialog.HlsScheme.Id);

        Assert.True(dialog.TryAccept());
        Assert.Equal([other], dialog.Plans.Keys);
        Assert.Equal(["Old"], dialog.Unconfigured);
    }

    [Fact]
    public async Task Additions_RunOneAtATime_AndTakeTheOnesAddedWhileRunning()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        using var main = OpenToAdd(gate, entered);

        await main.OpenAddDialogAsync([Missing("a")]);
        await main.OpenAddDialogAsync([Missing("b"), Missing("b2")]);
        Assert.Equal([AddTaskState.Waiting, AddTaskState.Waiting], main.AddTasks.Select(t => t.State));
        Assert.Equal(["a.mkv", "b.mkv 等 2 项"], main.AddTasks.Select(t => t.Title));
        Assert.Equal("Old", main.AddTasks[0].LibraryName);
        Assert.True(main.IsEngineInUse);

        var run = main.StartAddsCommand.ExecuteAsync(null);
        await entered.Task;
        Assert.Equal([AddTaskState.Running, AddTaskState.Waiting], main.AddTasks.Select(t => t.State));
        Assert.False(main.StartAddsCommand.CanExecute(null));

        await main.OpenAddDialogAsync([Missing("c")]);
        Assert.Equal(AddTaskState.Waiting, main.AddTasks[2].State);

        gate.SetResult();
        await run;
        Assert.All(main.AddTasks, t => Assert.Equal(AddTaskState.Failed, t.State));
        Assert.All(main.AddTasks, t => Assert.Contains("路径不存在", t.Error, StringComparison.Ordinal));
        Assert.False(main.IsAdding);
        Assert.False(main.IsEngineInUse);

        main.ClearFinishedAddsCommand.Execute(null);
        Assert.Empty(main.AddTasks);
    }

    [Fact]
    public async Task CancellingAnAddition_GoesOnWithTheNext()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        using var main = OpenToAdd(gate, entered);
        await main.OpenAddDialogAsync([Missing("a")]);
        await main.OpenAddDialogAsync([Missing("b")]);

        var run = main.StartAddsCommand.ExecuteAsync(null);
        await entered.Task;
        main.CancelAddTaskCommand.Execute(main.AddTasks[0]);
        gate.SetResult();
        await run;

        Assert.Equal([AddTaskState.Cancelled, AddTaskState.Failed], main.AddTasks.Select(t => t.State));
        Assert.False(main.IsEngineInUse);
    }

    [Fact]
    public async Task StartingAgain_RunsTheFailedAndTheCancelled_AndKeepsTheDone()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        using var main = OpenToAdd(gate, entered);
        await main.OpenAddDialogAsync([Missing("a")]);
        await main.OpenAddDialogAsync([Missing("b")]);
        await main.OpenAddDialogAsync([Missing("c")]);

        // c is done, as if it had run.
        var done = main.AddTasks[2];
        using (var cancel = new CancellationTokenSource())
        {
            done.Start(cancel);
            done.Complete(new AddResult(1, 0));
        }

        Assert.True(done.ReleaseEngineOnce());
        main.ReleaseEngine();

        var run = main.StartAddsCommand.ExecuteAsync(null);
        await entered.Task;
        main.CancelAddTaskCommand.Execute(main.AddTasks[0]);
        gate.SetResult();
        await run;
        Assert.Equal([AddTaskState.Cancelled, AddTaskState.Failed, AddTaskState.Done], main.AddTasks.Select(t => t.State));
        Assert.False(main.IsEngineInUse);
        Assert.True(main.StartAddsCommand.CanExecute(null));

        await main.StartAddsCommand.ExecuteAsync(null);

        Assert.Equal([AddTaskState.Failed, AddTaskState.Failed, AddTaskState.Done], main.AddTasks.Select(t => t.State));
        Assert.All(main.AddTasks.Take(2), t => Assert.Contains("路径不存在", t.Error, StringComparison.Ordinal));
        Assert.Equal(new AddResult(1, 0), done.Result);
        Assert.False(main.IsAdding);
        Assert.False(main.IsEngineInUse);
    }

    [Fact]
    public async Task AnAdditionOfADeletedDirectory_IsCancelledForGood_AndTheNextRuns()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        using var main = OpenToAdd(gate, new TaskCompletionSource());
        var deleted = new ConnectionSettings { Id = Guid.NewGuid(), Name = "gone", Type = ConnectionType.Worker, LocalRoot = _directory };
        var request = new AddRequest(_library, null, [Missing("a")], new SourcePlan([new PlanTarget(deleted, string.Empty, "gone:")], null));
        main.AddTasks.Add(new AddTaskViewModel(main.AcquireEngine()!, request, "Old"));
        await main.OpenAddDialogAsync([Missing("b")]);

        await main.StartAddsCommand.ExecuteAsync(null);

        var refused = main.AddTasks[0];
        Assert.Equal([AddTaskState.Cancelled, AddTaskState.Failed], main.AddTasks.Select(t => t.State));
        Assert.False(refused.CanRetry);
        Assert.Equal("已取消：目录 gone: 已删除，不能执行，请重新添加", refused.StatusText);
        Assert.False(main.IsEngineInUse);

        // Only the one that failed is run again.
        await main.StartAddsCommand.ExecuteAsync(null);
        Assert.Equal([AddTaskState.Cancelled, AddTaskState.Failed], main.AddTasks.Select(t => t.State));
        main.ClearFinishedAddsCommand.Execute(null);
        main.AddTasks.Add(refused);
        Assert.False(main.StartAddsCommand.CanExecute(null));
    }

    [Fact]
    public async Task AFolderPutInTheList_ShowsItsFiles_BeforeItRuns()
    {
        using var main = OpenToAdd(new TaskCompletionSource(), new TaskCompletionSource());
        var folder = Path.Combine(_directory, "Show");
        Directory.CreateDirectory(Path.Combine(folder, "S01"));
        File.WriteAllText(Path.Combine(folder, "S01", "E01.mkv"), "v");
        File.WriteAllText(Path.Combine(folder, "S01", "E01.srt"), "s");

        await main.OpenAddDialogAsync([folder]);

        var task = main.AddTasks[0];
        Assert.Equal(AddTaskState.Waiting, task.State);
        Assert.True(task.HasFiles);
        Assert.Equal(["Show/S01/E01.srt", "Show/S01/E01.mkv"], task.Files.Select(f => f.Path));
        Assert.All(task.Files, f => Assert.Equal("等待", f.StatusText));
    }

    [Fact]
    public async Task CancellingAll_CancelsTheRunningAndTheWaiting()
    {
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        using var main = OpenToAdd(gate, entered);
        await main.OpenAddDialogAsync([Missing("a")]);
        await main.OpenAddDialogAsync([Missing("b")]);
        await main.OpenAddDialogAsync([Missing("c")]);

        var run = main.StartAddsCommand.ExecuteAsync(null);
        await entered.Task;
        main.CancelAllAddsCommand.Execute(null);
        Assert.Equal([AddTaskState.Running, AddTaskState.Cancelled, AddTaskState.Cancelled], main.AddTasks.Select(t => t.State));
        gate.SetResult();
        await run;

        Assert.All(main.AddTasks, t => Assert.Equal(AddTaskState.Cancelled, t.State));
        Assert.False(main.IsAdding);
        Assert.False(main.IsEngineInUse);
    }

    [Fact]
    public async Task RemovingWaitingAdditions_ReleasesTheEngine()
    {
        using var main = OpenToAdd(new TaskCompletionSource(), new TaskCompletionSource());
        await main.OpenAddDialogAsync([Missing("a")]);
        await main.OpenAddDialogAsync([Missing("b")]);

        main.RemoveAddTaskCommand.Execute(main.AddTasks[0]);
        Assert.True(main.IsEngineInUse);
        main.RemoveAddTaskCommand.Execute(main.AddTasks[0]);

        Assert.Empty(main.AddTasks);
        Assert.False(main.IsEngineInUse);
        Assert.False(main.StartAddsCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnAddition_ShowsEachFile_ItsStep_AndTheTimeLeft()
    {
        Assert.Equal("512 B", AddFileViewModel.Size(512));
        Assert.Equal("1.5 KB", AddFileViewModel.Size(1536));
        Assert.Equal("3.10 GB", AddFileViewModel.Size(3.1 * 1024 * 1024 * 1024));
        Assert.Equal("01:23", AddFileViewModel.Duration(TimeSpan.FromSeconds(83.9)));
        Assert.Equal("1:30:00", AddFileViewModel.Duration(TimeSpan.FromHours(1.5)));
        Assert.Equal("0:02:05", AddFileViewModel.Clock(125_500));

        using var main = OpenToAdd(new TaskCompletionSource(), new TaskCompletionSource());
        await main.OpenAddDialogAsync([Missing("a")]);
        var task = main.AddTasks[0];
        using var cancel = new CancellationTokenSource();
        task.Start(cancel);

        task.Apply(new AddFilesListed(["a.mkv", "b.srt"]), TimeSpan.Zero);
        Assert.True(task.HasFiles);
        Assert.Equal("进行中 0/2", task.StatusText);

        task.Apply(new AddStepStarted(0, AddStep.Upload, "B2"), TimeSpan.Zero);
        Assert.Same(task.Files[0], task.Current);
        Assert.True(task.ShowCurrent);
        Assert.Equal("上传到 B2", task.Current!.StatusText);
        Assert.Equal("已用 00:00", task.Current.TimeText);

        task.Apply(new AddStepAdvanced(0, 50, 100, ProgressUnit.Bytes), TimeSpan.FromSeconds(10));
        Assert.Equal(50, task.Files[0].Percent);
        Assert.StartsWith("50%", task.Files[0].DetailText, StringComparison.Ordinal);
        Assert.Equal("已用 00:10 · 剩余约 00:10", task.Files[0].TimeText);

        task.IsExpanded = true;
        Assert.False(task.ShowCurrent);

        task.Apply(new AddFileEnded(0, true, null), TimeSpan.FromSeconds(20));
        Assert.Equal("进行中 1/2", task.StatusText);
        Assert.Null(task.Current);
        Assert.Equal("完成", task.Files[0].StatusText);

        task.Complete(new AddResult(1, 0));
        Assert.Equal("失败：未完成", task.Files[1].StatusText);
    }

    private string Missing(string name) => Path.Combine(_directory, name + ".mkv");

    // The first check of the protocol waits for the gate, so that the first addition stays running until it opens.
    private MainViewModel OpenToAdd(TaskCompletionSource gate, TaskCompletionSource entered)
    {
        var checks = 0;
        var server = new FakeServer(async (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Protocol", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref checks) == 1)
                {
                    entered.TrySetResult();
                    await gate.Task;
                }

                return FakeServer.Json(new ProtocolVersion(3));
            }

            return path switch
            {
                _ when path.EndsWith("/Workers", StringComparison.Ordinal) => FakeServer.Json(new WorkerRegistered(_worker)),
                "/Library/VirtualFolders" => FakeServer.Json(new[] { new VirtualFolder("Old", _library.ToString(), ["/a"]) }),
                "/HlsWorker/Libraries" => FakeServer.Json(Array.Empty<WorkerLibrary>()),
                _ => FakeServer.Status(HttpStatusCode.NotFound)
            };
        });
        var main = Open(server);
        main.Dialogs = new FakeDialogs();

        // The server gives back the id the worker has: no id to be set on the UI thread.
        main.WorkerIdText = _worker.ToString("N");
        return main;
    }

    private MainViewModel Open(HttpMessageHandler? handler = null)
    {
        new SettingsStore(_paths).Write(new WorkerSettings
        {
            Server = "http://jellyfin",
            ApiKey = "k",
            Connections =
            [
                new ConnectionSettings { Id = _s3, Name = "s3", Type = ConnectionType.S3, Endpoint = "http://s3", Bucket = "media", AccessKey = "a", SecretKey = "s" },
                new ConnectionSettings { Id = _backup, Name = "backup", Type = ConnectionType.Worker, LocalRoot = _directory }
            ],
            Schemes =
            [
                new SchemeSettings
                {
                    Id = _scheme,
                    Name = "a",
                    Hls =
                    [
                        new SchemeEntry { ConnectionId = _s3, Declare = true },
                        new SchemeEntry { ConnectionId = _backup }
                    ],
                    Sources = [new SchemeEntry { ConnectionId = _backup }, new SchemeEntry { ConnectionId = _s3, Declare = true }]
                }
            ],
            Libraries = [new LibrarySettings { Id = _library, Name = "Old", SchemeId = _scheme }]
        });
        return new MainViewModel(_paths, handler);
    }
}

/// <summary>
/// Accepts every dialog with the first library.
/// </summary>
internal sealed class FakeDialogs : IDialogs
{
    public async Task<bool> ShowAddDialogAsync(AddDialogViewModel dialog)
    {
        await dialog.LoadAsync();
        dialog.SelectedLibrary = dialog.Libraries.FirstOrDefault();
        return dialog.TryAccept();
    }

    public Task<bool> ConfirmAsync(string message, string accept, string cancel) => Task.FromResult(true);
}
