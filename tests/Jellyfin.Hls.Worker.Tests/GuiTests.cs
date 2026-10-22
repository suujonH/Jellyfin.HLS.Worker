using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Gui.ViewModels;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class GuiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-gui-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _library = Guid.NewGuid();

    public GuiTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Rename_ThroughAnEmptyNameAndAnotherConnectionsName_MovesOnlyItsOwnReferences()
    {
        using var main = Open();
        var backup = main.Connections.Single(c => c.Name == "backup");

        foreach (var name in new[] { string.Empty, "s", "s3", "s3-", "s3-new" })
        {
            backup.Name = name;
        }

        Assert.Equal("s3-new", main.Settings.Hls!.Connection);
        Assert.Equal("s3", main.Settings.Libraries.Single().Connection);
        Assert.True(main.TrySave());
        WorkerSettings.Load(main.ConfigPath);
    }

    [Fact]
    public void Rename_IntoTheNameAnotherIsStillKnownBy_WaitsUntilThatOneIsRenamed()
    {
        using var main = Open();
        var s3 = main.Connections.Single(c => c.Name == "s3");
        var backup = main.Connections.Single(c => c.Name == "backup");

        s3.Name = string.Empty;
        backup.Name = "s3";
        Assert.Equal("s3", main.Settings.Libraries.Single().Connection);
        Assert.Equal("backup", main.Settings.Hls!.Connection);

        s3.Name = "c";

        Assert.Equal("c", main.Settings.Libraries.Single().Connection);
        Assert.Equal("s3", main.Settings.Hls!.Connection);
        Assert.Equal(["c", "s3"], main.Connections.Select(c => c.Reference).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AConnectionNoLongerS3_IsNoLongerTheDefaultOfALibrary()
    {
        using var main = Open();

        main.Connections.Single(c => c.Name == "s3").Type = ConnectionType.Local;

        Assert.Empty(main.Settings.Libraries);
        Assert.Equal("backup", main.Settings.Hls!.Connection);
    }

    [Fact]
    public void RemovingAConnection_DropsTheDefaultsOfItsLibraries_AndANewOneTakesAFreeName()
    {
        using var main = Open();
        main.SelectedConnection = main.Connections.Single(c => c.Name == "s3");

        main.RemoveConnectionCommand.Execute(null);
        main.NewConnectionCommand.Execute(null);
        main.NewConnectionCommand.Execute(null);

        Assert.Empty(main.Settings.Libraries);
        Assert.Equal(3, main.Connections.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void RemovingAConnection_LetsTheOneWaitingForItsNameTakeItUp()
    {
        using var main = Open();
        var backup = main.Connections.Single(c => c.Name == "backup");
        backup.Name = "s3";
        main.SelectedConnection = main.Connections.Single(c => c.Reference == "s3");

        main.RemoveConnectionCommand.Execute(null);

        Assert.Equal("s3", backup.Reference);
        Assert.Equal("s3", main.Settings.Hls!.Connection);
        Assert.Empty(main.Settings.Libraries);
    }

    [Fact]
    public void AConnectionRetypedWhileItsNameIsPending_IsNoLongerTheDefaultOfALibrary()
    {
        using var main = Open();
        var s3 = main.Connections.Single(c => c.Name == "s3");

        s3.Name = string.Empty;
        s3.Type = ConnectionType.Local;
        s3.Name = "c";

        Assert.Empty(main.Settings.Libraries);
        Assert.Equal("c", s3.Reference);
    }

    [Fact]
    public void SwappingTwoNames_IsNotSavedWhileARenameWaits()
    {
        using var main = Open();
        var s3 = main.Connections.Single(c => c.Name == "s3");
        var backup = main.Connections.Single(c => c.Name == "backup");

        s3.Name = "backup";
        backup.Name = "s3";

        Assert.False(main.TrySave());
        Assert.Equal("s3", main.Settings.Libraries.Single().Connection);
    }

    [Fact]
    public void ANewConnection_DoesNotTakeTheNameAWaitingOneIsKnownBy()
    {
        using var main = Open();
        main.NewConnectionCommand.Execute(null);
        var third = main.Connections.Single(c => c.Name == "连接3");
        third.Name = string.Empty;

        main.NewConnectionCommand.Execute(null);

        Assert.DoesNotContain(main.Connections, c => c != third && c.Name == "连接3");
    }

    [Fact]
    public void TheChosenDestination_FollowsARenameThatWaited()
    {
        using var main = Open();
        var s3 = main.Connections.Single(c => c.Name == "s3");
        main.Add.SelectedDestination = main.Add.Destinations.Single(d => d.Name == "s3");

        s3.Name = string.Empty;
        main.Connections.Single(c => c.Name == "backup").Name = "b";
        s3.Name = "c";

        Assert.Equal("c", main.Add.SelectedDestination?.Name);
    }

    [Fact]
    public void TheHlsConnection_ChosenWhileARenameWaits_FollowsThatConnection()
    {
        using var main = Open();
        var backup = main.Connections.Single(c => c.Name == "backup");
        backup.Name = "s3";

        main.HlsConnection = backup;
        backup.Name = "archive";

        Assert.Equal("archive", main.Settings.Hls!.Connection);
        Assert.Same(backup, main.HlsConnection);
    }

    private MainViewModel Open()
    {
        var path = Path.Combine(_directory, "worker.json");
        new WorkerSettings
        {
            ApiKey = "k",
            Connections =
            [
                new ConnectionSettings { Name = "s3", Type = ConnectionType.S3, Endpoint = "http://s3", Bucket = "media", AccessKey = "a", SecretKey = "s" },
                new ConnectionSettings { Name = "backup", Type = ConnectionType.Local, LocalRoot = _directory, ServerRoot = "/srv/backup" }
            ],
            Hls = new HlsTargetSettings { Connection = "backup", Path = "hls" },
            Libraries = [new LibrarySettings { Id = _library, Connection = "s3" }]
        }.Save(path);
        return new MainViewModel(path);
    }
}
