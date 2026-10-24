using Jellyfin.Hls.Worker.Jobs;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class UploadLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-uploads-" + Guid.NewGuid().ToString("N"));

    public UploadLogTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Recent_GivesTheLatestFirst_AsTheyWereAdded()
    {
        var log = new UploadLog(Path.Combine(_directory, "sqlite.db"));
        var library = Guid.NewGuid();
        var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        log.Add(Enumerable.Range(0, 1000)
            .Select(i => new UploadRecord(time.AddSeconds(i), UploadKind.Hls, library, "segment-" + i, "b2:hls/m/" + i, i, true, null))
            .ToList());
        log.Add([new UploadRecord(time.AddHours(1), UploadKind.Shortcut, null, "Film.mkv", "Jellyfin 媒体库/Film.strm", 5, false, "refused")]);
        log.Add([]);

        var recent = log.Recent(3);

        Assert.Equal(new UploadRecord(time.AddHours(1), UploadKind.Shortcut, null, "Film.mkv", "Jellyfin 媒体库/Film.strm", 5, false, "refused"), recent[0]);
        Assert.Equal(new UploadRecord(time.AddSeconds(999), UploadKind.Hls, library, "segment-999", "b2:hls/m/999", 999, true, null), recent[1]);
        Assert.Equal("segment-998", recent[2].LocalPath);
        Assert.Equal(1001, log.Recent(5000).Count);
    }

    [Fact]
    public void ALogOfAnEarlierVersion_GetsTheNewColumns_AndKeepsItsRecordsWithoutOperation()
    {
        var path = Path.Combine(_directory, "sqlite.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Uploads (Id INTEGER PRIMARY KEY AUTOINCREMENT, Time TEXT NOT NULL, Kind INTEGER NOT NULL, LibraryId TEXT NULL,
                    LocalPath TEXT NOT NULL, Destination TEXT NOT NULL, Size INTEGER NOT NULL, Succeeded INTEGER NOT NULL, Error TEXT NULL);
                INSERT INTO Uploads (Time, Kind, LibraryId, LocalPath, Destination, Size, Succeeded, Error)
                VALUES ('2026-10-01T12:00:00.0000000Z', 0, NULL, 'Film.mkv', 'b2/Film.mkv', 5, 1, NULL);
                """;
            command.ExecuteNonQuery();
        }

        var log = new UploadLog(path);
        log.Add([new UploadRecord(DateTime.UtcNow, UploadKind.Hls, null, "s", "d", 1, true, null, Operation(log).Id, Guid.Empty, "hls/s")]);

        var old = Assert.Single(log.RecentWithoutOperation(10));
        Assert.Equal(("Film.mkv", (string?)null, (Guid?)null, (string?)null, UploadState.Uploaded), (old.LocalPath, old.OperationId, old.ConnectionId, old.StoragePath, old.State));
        Assert.Equal(2, log.Recent(10).Count);
        _ = new UploadLog(path);
    }

    [Fact]
    public void AnOperationCommitted_GivesWhatItUploadedToTheServer()
    {
        var log = new UploadLog(Path.Combine(_directory, "sqlite.db"));
        var operation = Operation(log);
        var connection = Guid.NewGuid();
        log.Add(
        [
            Record(operation.Id, UploadKind.Source, true, connection, "movies/Film.mkv"),
            Record(operation.Id, UploadKind.Hls, true, Guid.Empty, "hls/a/1.m4s"),
            Record(operation.Id, UploadKind.Hls, false, Guid.Empty, "hls/a/2.m4s")
        ]);

        Assert.Equal(["movies/Film.mkv", "hls/a/1.m4s"], log.GetUncollected(operation.Id).Select(u => u.StoragePath));
        log.Collect(operation.Id, UploadKind.Hls);
        Assert.Equal(["movies/Film.mkv"], log.GetUncollected(operation.Id).Select(u => u.StoragePath));
        Assert.False(log.IsCollected(connection, "movies/Film.mkv"));

        log.EndOperation(operation.Id, OperationState.Committed, null);

        Assert.Empty(log.GetUncollected(operation.Id));
        Assert.True(log.IsCollected(connection, "movies/Film.mkv"));
        Assert.Equal([UploadState.Collected, UploadState.Collected, UploadState.Uploaded], log.ForOperation(operation.Id).Select(r => r.State));
        var ended = log.GetOperation(operation.Id)!;
        Assert.Equal(OperationState.Committed, ended.State);
        Assert.NotNull(ended.Ended);
    }

    [Fact]
    public void Adopt_GivesTheRecordsTheJobAndTheDirectoryOfTheEarlierOperation_AndRemovesIt()
    {
        var log = new UploadLog(Path.Combine(_directory, "sqlite.db"));
        var earlier = Operation(log);
        var job = Guid.NewGuid();
        log.SetJob(earlier.Id, job, "token");
        log.SetDirectory(earlier.Id, "dir");
        log.Add([Record(earlier.Id, UploadKind.Hls, true, Guid.Empty, "hls/a/1.m4s")]);
        log.EndOperation(earlier.Id, OperationState.Failed, "ffmpeg");
        var later = Operation(log);

        log.Adopt(earlier.Id, later.Id);

        Assert.Null(log.GetOperation(earlier.Id));
        var adopted = log.GetOperation(later.Id)!;
        Assert.Equal(((Guid?)job, "token", "dir", OperationState.Running), (adopted.JobId, adopted.LeaseToken, adopted.Directory, adopted.State));
        Assert.Equal(["hls/a/1.m4s"], log.GetUncollected(later.Id).Select(u => u.StoragePath));
    }

    [Fact]
    public void Stale_AreTheAdditionsOfTheSameFile_ThatDidNotEndWell_AndWereNotCleaned()
    {
        var log = new UploadLog(Path.Combine(_directory, "sqlite.db"));
        var library = Guid.NewGuid();
        var failed = Operation(log, library);
        log.EndOperation(failed.Id, OperationState.Failed, "x");
        var abandoned = Operation(log, library);
        log.EndOperation(abandoned.Id, OperationState.Abandoned, "x");
        var cleaned = Operation(log, library);
        log.EndOperation(cleaned.Id, OperationState.Cancelled, null);
        log.SetCleaned(cleaned.Id);
        var committed = Operation(log, library);
        log.EndOperation(committed.Id, OperationState.Committed, null);
        _ = Operation(log, library);
        _ = Operation(log, library, entryPath: "Other.strm");
        var otherLocation = Operation(log, library, location: "/b");
        log.EndOperation(otherLocation.Id, OperationState.Failed, "x");
        var current = Operation(log, library);
        log.EndOperation(current.Id, OperationState.Failed, "x");

        Assert.Equal([failed.Id, abandoned.Id], log.GetStale(library, "/a", "Film.strm", current.Id).Select(o => o.Id));
        Assert.Equal([otherLocation.Id], log.GetStale(library, "/b", "Film.strm", current.Id).Select(o => o.Id));
    }

    [Fact]
    public void EndInterrupted_CancelsTheRunningOperationsOfOtherSessionsOnly()
    {
        var path = Path.Combine(_directory, "sqlite.db");
        var log = new UploadLog(path);
        var mine = Operation(log);
        var other = Operation(log);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Operations SET Session = 'gone' WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", other.Id);
            command.ExecuteNonQuery();
        }

        Assert.Equal(1, log.EndInterrupted());

        Assert.Equal(OperationState.Running, log.GetOperation(mine.Id)!.State);
        Assert.Equal(OperationState.Cancelled, log.GetOperation(other.Id)!.State);
        Assert.Equal(0, log.EndInterrupted());
    }

    [Fact]
    public void RecentOperations_CountTheirRecords_TheLatestFirst()
    {
        var log = new UploadLog(Path.Combine(_directory, "sqlite.db"));
        var first = Operation(log, started: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = Operation(log, started: new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        log.Add(
        [
            Record(first.Id, UploadKind.Source, true, Guid.NewGuid(), "a", 10),
            Record(first.Id, UploadKind.Hls, true, Guid.Empty, "b", 5),
            Record(first.Id, UploadKind.Hls, false, Guid.Empty, "c", 7)
        ]);

        var recent = log.RecentOperations(10);

        Assert.Equal([second.Id, first.Id], recent.Select(r => r.Operation.Id));
        Assert.Equal((0, 0, 0L), (recent[0].Files, recent[0].Failed, recent[0].Bytes));
        Assert.Equal((3, 1, 15L), (recent[1].Files, recent[1].Failed, recent[1].Bytes));
    }

    private static UploadOperation Operation(UploadLog log, Guid? library = null, string location = "/a", string entryPath = "Film.strm", DateTime? started = null)
    {
        var operation = new UploadOperation(
            Guid.NewGuid().ToString("N"),
            OperationKind.Add,
            started ?? DateTime.UtcNow,
            null,
            OperationState.Running,
            null,
            library ?? Guid.NewGuid(),
            location,
            entryPath,
            "C:/Film.mkv",
            5,
            "sha",
            null,
            null,
            null,
            false);
        log.StartOperation(operation);
        return operation;
    }

    private static UploadRecord Record(string operation, UploadKind kind, bool succeeded, Guid connection, string storagePath, long size = 1)
        => new(DateTime.UtcNow, kind, null, "local", "d/" + storagePath, size, succeeded, succeeded ? null : "x", operation, connection, storagePath);
}
