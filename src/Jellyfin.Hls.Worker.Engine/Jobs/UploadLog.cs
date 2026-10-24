using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>What a file uploaded is.</summary>
public enum UploadKind
{
    /// <summary>An original file, to a source directory.</summary>
    Source = 0,

    /// <summary>A file of a rendition, to an HLS directory.</summary>
    Hls = 1,

    /// <summary>A file written into the folder of a library through the server.</summary>
    LibraryFile = 2,

    /// <summary>A .strm file written into the folder of a library through the server.</summary>
    Shortcut = 3,

    /// <summary>A file of a TrueCache entry written into the folder of a library through the server.</summary>
    TrueCache = 4
}

/// <summary>What an operation is.</summary>
public enum OperationKind
{
    /// <summary>The addition of a file to a library.</summary>
    Add = 0,

    /// <summary>A job claimed from the server.</summary>
    Job = 1
}

/// <summary>Where an operation is.</summary>
public enum OperationState
{
    /// <summary>It runs, or the worker stopped while it ran.</summary>
    Running = 0,

    /// <summary>It ended well: what it uploaded is the server's.</summary>
    Committed = 1,

    /// <summary>It failed.</summary>
    Failed = 2,

    /// <summary>It was cancelled.</summary>
    Cancelled = 3,

    /// <summary>Another operation took its file over.</summary>
    Abandoned = 4
}

/// <summary>Where a file uploaded is.</summary>
public enum UploadState
{
    /// <summary>Uploaded, and not yet taken by the server.</summary>
    Uploaded = 0,

    /// <summary>Taken by the server: it is not removed again.</summary>
    Collected = 1,

    /// <summary>Removed by a later addition of the file.</summary>
    Removed = 2
}

/// <summary>
/// A file uploaded, or that failed to be.
/// </summary>
/// <param name="Time">When, in UTC.</param>
/// <param name="Kind">What it is.</param>
/// <param name="LibraryId">The library it is of, if known.</param>
/// <param name="LocalPath">The file on this machine.</param>
/// <param name="Destination">Where it went, as the log shows it.</param>
/// <param name="Size">Its size.</param>
/// <param name="Succeeded">Whether it was uploaded.</param>
/// <param name="Error">Why not.</param>
/// <param name="OperationId">The operation it was uploaded for; <c>null</c> for the records written before operations.</param>
/// <param name="ConnectionId">The connection it went to, <see cref="Guid.Empty"/> for the HLS directory of this machine; <c>null</c> for a file written through the server.</param>
/// <param name="StoragePath">Its path on that connection; <c>null</c> for a file written through the server.</param>
/// <param name="State">Where it is.</param>
public sealed record UploadRecord(
    DateTime Time,
    UploadKind Kind,
    Guid? LibraryId,
    string LocalPath,
    string Destination,
    long Size,
    bool Succeeded,
    string? Error,
    string? OperationId = null,
    Guid? ConnectionId = null,
    string? StoragePath = null,
    UploadState State = UploadState.Uploaded);

/// <summary>
/// An operation: the addition of a file, or a job claimed.
/// </summary>
/// <param name="Id">Its id, 32 lower-case hexadecimal characters.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Started">When it started, in UTC.</param>
/// <param name="Ended">When it ended, in UTC.</param>
/// <param name="State">Where it is.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="LibraryId">The library.</param>
/// <param name="Location">The folder of the library; <c>null</c> for the only one.</param>
/// <param name="EntryPath">The path of the file in the folder of the library, with forward slashes; for a job, the path the server gives.</param>
/// <param name="LocalPath">The file on this machine; for a job, the path the server gives.</param>
/// <param name="Size">The size of the file.</param>
/// <param name="Sha256">The SHA-256 of a video added.</param>
/// <param name="JobId">The job of the server it encodes with.</param>
/// <param name="LeaseToken">The lease token of that job.</param>
/// <param name="Directory">The directory of this machine it keeps what it made in.</param>
/// <param name="Cleaned">Whether what it left was removed by a later addition.</param>
public sealed record UploadOperation(
    string Id,
    OperationKind Kind,
    DateTime Started,
    DateTime? Ended,
    OperationState State,
    string? Error,
    Guid? LibraryId,
    string? Location,
    string EntryPath,
    string LocalPath,
    long Size,
    string? Sha256,
    Guid? JobId,
    string? LeaseToken,
    string? Directory,
    bool Cleaned);

/// <summary>An operation with the number of its records, of those that failed, and the bytes uploaded.</summary>
/// <param name="Operation">The operation.</param>
/// <param name="Files">Its records.</param>
/// <param name="Failed">Those that failed.</param>
/// <param name="Bytes">The bytes uploaded.</param>
public sealed record OperationSummary(UploadOperation Operation, int Files, int Failed, long Bytes);

/// <summary>A file uploaded to a connection and not yet taken by the server.</summary>
/// <param name="Id">The record.</param>
/// <param name="Kind">What it is.</param>
/// <param name="ConnectionId">The connection, <see cref="Guid.Empty"/> for the HLS directory of this machine.</param>
/// <param name="StoragePath">Its path on the connection.</param>
public sealed record StoredUpload(long Id, UploadKind Kind, Guid ConnectionId, string StoragePath);

/// <summary>
/// The log of the files uploaded, one row each, a rendition being a row per segment, and of the operations they were uploaded for.
/// </summary>
public sealed class UploadLog
{
    private const string RecordColumns = "Time, Kind, LibraryId, LocalPath, Destination, Size, Succeeded, Error, OperationId, ConnectionId, StoragePath, State";

    private const string OperationColumns = "Id, Kind, Started, Ended, State, Error, LibraryId, Location, EntryPath, LocalPath, Size, Sha256, JobId, LeaseToken, Directory, Cleaned";

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadLog"/> class, creating or upgrading the tables if needed; no row is changed.
    /// </summary>
    /// <param name="path">The database file.</param>
    public UploadLog(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Uploads (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Time TEXT NOT NULL,
                Kind INTEGER NOT NULL,
                LibraryId TEXT NULL,
                LocalPath TEXT NOT NULL,
                Destination TEXT NOT NULL,
                Size INTEGER NOT NULL,
                Succeeded INTEGER NOT NULL,
                Error TEXT NULL);
            """);
        EnsureColumn(connection, "OperationId", "TEXT NULL");
        EnsureColumn(connection, "ConnectionId", "TEXT NULL");
        EnsureColumn(connection, "StoragePath", "TEXT NULL");
        EnsureColumn(connection, "State", "INTEGER NOT NULL DEFAULT 0");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Operations (
                Id TEXT PRIMARY KEY,
                Kind INTEGER NOT NULL,
                Session TEXT NOT NULL,
                Started TEXT NOT NULL,
                Ended TEXT NULL,
                State INTEGER NOT NULL,
                Error TEXT NULL,
                LibraryId TEXT NULL,
                Location TEXT NULL,
                EntryPath TEXT NOT NULL,
                LocalPath TEXT NOT NULL,
                Size INTEGER NOT NULL,
                Sha256 TEXT NULL,
                JobId TEXT NULL,
                LeaseToken TEXT NULL,
                Directory TEXT NULL,
                Cleaned INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS IX_Uploads_OperationId ON Uploads (OperationId);
            CREATE INDEX IF NOT EXISTS IX_Uploads_Storage ON Uploads (ConnectionId, StoragePath);
            CREATE INDEX IF NOT EXISTS IX_Operations_Entry ON Operations (LibraryId, EntryPath);
            """);
    }

    /// <summary>Gets the session of this process: the operations of another one that still run were stopped with it.</summary>
    public static string Session { get; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Adds records, in one transaction.
    /// </summary>
    /// <param name="records">The records.</param>
    public void Add(IReadOnlyCollection<UploadRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Uploads (Time, Kind, LibraryId, LocalPath, Destination, Size, Succeeded, Error, OperationId, ConnectionId, StoragePath, State)
            VALUES ($time, $kind, $library, $local, $destination, $size, $succeeded, $error, $operation, $connection, $storage, $state);
            """;
        var time = command.Parameters.Add("$time", SqliteType.Text);
        var kind = command.Parameters.Add("$kind", SqliteType.Integer);
        var library = command.Parameters.Add("$library", SqliteType.Text);
        var local = command.Parameters.Add("$local", SqliteType.Text);
        var destination = command.Parameters.Add("$destination", SqliteType.Text);
        var size = command.Parameters.Add("$size", SqliteType.Integer);
        var succeeded = command.Parameters.Add("$succeeded", SqliteType.Integer);
        var error = command.Parameters.Add("$error", SqliteType.Text);
        var operation = command.Parameters.Add("$operation", SqliteType.Text);
        var connectionId = command.Parameters.Add("$connection", SqliteType.Text);
        var storage = command.Parameters.Add("$storage", SqliteType.Text);
        var state = command.Parameters.Add("$state", SqliteType.Integer);
        foreach (var record in records)
        {
            time.Value = Format(record.Time);
            kind.Value = (int)record.Kind;
            library.Value = Format(record.LibraryId);
            local.Value = record.LocalPath;
            destination.Value = record.Destination;
            size.Value = record.Size;
            succeeded.Value = record.Succeeded ? 1 : 0;
            error.Value = (object?)record.Error ?? DBNull.Value;
            operation.Value = (object?)record.OperationId ?? DBNull.Value;
            connectionId.Value = Format(record.ConnectionId);
            storage.Value = (object?)record.StoragePath ?? DBNull.Value;
            state.Value = (int)record.State;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Gets the latest records, the latest first.
    /// </summary>
    /// <param name="limit">How many at most.</param>
    /// <returns>The records.</returns>
    public IReadOnlyList<UploadRecord> Recent(int limit)
        => ReadRecords("SELECT " + RecordColumns + " FROM Uploads ORDER BY Id DESC LIMIT $limit;", ("$limit", limit));

    /// <summary>Gets the records of an operation, the first first.</summary>
    /// <param name="id">The operation.</param>
    /// <returns>The records.</returns>
    public IReadOnlyList<UploadRecord> ForOperation(string id)
        => ReadRecords("SELECT " + RecordColumns + " FROM Uploads WHERE OperationId = $id ORDER BY Id;", ("$id", id));

    /// <summary>Gets the latest records written before operations, the latest first.</summary>
    /// <param name="limit">How many at most.</param>
    /// <returns>The records.</returns>
    public IReadOnlyList<UploadRecord> RecentWithoutOperation(int limit)
        => ReadRecords("SELECT " + RecordColumns + " FROM Uploads WHERE OperationId IS NULL ORDER BY Id DESC LIMIT $limit;", ("$limit", limit));

    /// <summary>Records an operation that starts, in the session of this process.</summary>
    /// <param name="operation">The operation.</param>
    public void StartOperation(UploadOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Operations (Id, Kind, Session, Started, Ended, State, Error, LibraryId, Location, EntryPath, LocalPath, Size, Sha256, JobId, LeaseToken, Directory, Cleaned)
            VALUES ($id, $kind, $session, $started, $ended, $state, $error, $library, $location, $entry, $local, $size, $sha, $job, $token, $directory, $cleaned);
            """;
        command.Parameters.AddWithValue("$id", operation.Id);
        command.Parameters.AddWithValue("$kind", (int)operation.Kind);
        command.Parameters.AddWithValue("$session", Session);
        command.Parameters.AddWithValue("$started", Format(operation.Started));
        command.Parameters.AddWithValue("$ended", operation.Ended is { } ended ? Format(ended) : DBNull.Value);
        command.Parameters.AddWithValue("$state", (int)operation.State);
        command.Parameters.AddWithValue("$error", (object?)operation.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$library", Format(operation.LibraryId));
        command.Parameters.AddWithValue("$location", (object?)operation.Location ?? DBNull.Value);
        command.Parameters.AddWithValue("$entry", operation.EntryPath);
        command.Parameters.AddWithValue("$local", operation.LocalPath);
        command.Parameters.AddWithValue("$size", operation.Size);
        command.Parameters.AddWithValue("$sha", (object?)operation.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$job", Format(operation.JobId));
        command.Parameters.AddWithValue("$token", (object?)operation.LeaseToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$directory", (object?)operation.Directory ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleaned", operation.Cleaned ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>Gets an operation.</summary>
    /// <param name="id">The operation.</param>
    /// <returns>The operation, or <c>null</c> when this machine has none of this id.</returns>
    public UploadOperation? GetOperation(string id)
        => ReadOperations("SELECT " + OperationColumns + " FROM Operations WHERE Id = $id;", ("$id", id)).FirstOrDefault();

    /// <summary>Records the job of the server an operation encodes with.</summary>
    /// <param name="id">The operation.</param>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseToken">Its lease token.</param>
    public void SetJob(string id, Guid jobId, string leaseToken)
        => Update("UPDATE Operations SET JobId = $job, LeaseToken = $token WHERE Id = $id;", ("$id", id), ("$job", Format(jobId)), ("$token", leaseToken));

    /// <summary>Records the directory of this machine an operation keeps what it made in.</summary>
    /// <param name="id">The operation.</param>
    /// <param name="directory">The directory.</param>
    public void SetDirectory(string id, string? directory)
        => Update("UPDATE Operations SET Directory = $directory WHERE Id = $id;", ("$id", id), ("$directory", (object?)directory ?? DBNull.Value));

    /// <summary>Ends an operation; one committed gives what it uploaded to the server.</summary>
    /// <param name="id">The operation.</param>
    /// <param name="state">How it ended.</param>
    /// <param name="error">Why it failed.</param>
    public void EndOperation(string id, OperationState state, string? error)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            "UPDATE Operations SET Ended = $now, State = $state, Error = $error WHERE Id = $id;",
            ("$id", id),
            ("$now", Format(DateTime.UtcNow)),
            ("$state", (int)state),
            ("$error", (object?)error ?? DBNull.Value));
        if (state == OperationState.Committed)
        {
            Execute(connection, transaction, "UPDATE Uploads SET State = 1 WHERE OperationId = $id AND Succeeded = 1 AND State = 0;", ("$id", id));
        }

        transaction.Commit();
    }

    /// <summary>Records that the server took the files of a kind an operation uploaded.</summary>
    /// <param name="id">The operation.</param>
    /// <param name="kind">The kind.</param>
    public void Collect(string id, UploadKind kind)
        => Update("UPDATE Uploads SET State = 1 WHERE OperationId = $id AND Kind = $kind AND Succeeded = 1 AND State = 0;", ("$id", id), ("$kind", (int)kind));

    /// <summary>Gives the records, the job and the directory of an earlier operation to the one that resumes it, which it replaces.</summary>
    /// <param name="previous">The earlier operation.</param>
    /// <param name="id">The operation that resumes it.</param>
    public void Adopt(string previous, string id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "UPDATE Uploads SET OperationId = $id WHERE OperationId = $previous;", ("$id", id), ("$previous", previous));
        Execute(
            connection,
            transaction,
            """
            UPDATE Operations SET
                JobId = (SELECT JobId FROM Operations WHERE Id = $previous),
                LeaseToken = (SELECT LeaseToken FROM Operations WHERE Id = $previous),
                Directory = (SELECT Directory FROM Operations WHERE Id = $previous)
            WHERE Id = $id;
            """,
            ("$id", id),
            ("$previous", previous));
        Execute(connection, transaction, "DELETE FROM Operations WHERE Id = $previous;", ("$previous", previous));
        transaction.Commit();
    }

    /// <summary>
    /// Gets the additions of the same file of a library that failed, were cancelled or taken over, and whose leftovers are not
    /// removed, the first first.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="location">The folder of the library; <c>null</c> for the only one.</param>
    /// <param name="entryPath">The path of the file in the folder.</param>
    /// <param name="except">The operation that asks.</param>
    /// <returns>The operations.</returns>
    public IReadOnlyList<UploadOperation> GetStale(Guid libraryId, string? location, string entryPath, string except)
        => ReadOperations(
            "SELECT " + OperationColumns + """
             FROM Operations
            WHERE Kind = 0 AND State IN (2, 3, 4) AND Cleaned = 0 AND Id <> $except
                AND LibraryId = $library AND Location IS $location AND EntryPath = $entry
            ORDER BY Started;
            """,
            ("$except", except),
            ("$library", Format(libraryId)),
            ("$location", (object?)location ?? DBNull.Value),
            ("$entry", entryPath));

    /// <summary>Gets the files an operation uploaded to a connection that the server did not take.</summary>
    /// <param name="id">The operation.</param>
    /// <returns>The files.</returns>
    public IReadOnlyList<StoredUpload> GetUncollected(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, ConnectionId, StoragePath FROM Uploads
            WHERE OperationId = $id AND Succeeded = 1 AND State = 0 AND ConnectionId IS NOT NULL AND StoragePath IS NOT NULL
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var uploads = new List<StoredUpload>();
        while (reader.Read())
        {
            uploads.Add(new StoredUpload(reader.GetInt64(0), (UploadKind)reader.GetInt32(1), Guid.ParseExact(reader.GetString(2), "N"), reader.GetString(3)));
        }

        return uploads;
    }

    /// <summary>Gets whether the server took a file uploaded to this path of a connection.</summary>
    /// <param name="connectionId">The connection.</param>
    /// <param name="storagePath">The path.</param>
    /// <returns>Whether it did.</returns>
    public bool IsCollected(Guid connectionId, string storagePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM Uploads WHERE ConnectionId = $connection AND StoragePath = $storage AND State = 1);";
        command.Parameters.AddWithValue("$connection", Format(connectionId));
        command.Parameters.AddWithValue("$storage", storagePath);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Records that files uploaded were removed.</summary>
    /// <param name="ids">The records.</param>
    public void MarkRemoved(IReadOnlyCollection<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Uploads SET State = 2 WHERE Id = $id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            parameter.Value = id;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Records that what an operation left was removed.</summary>
    /// <param name="id">The operation.</param>
    public void SetCleaned(string id) => Update("UPDATE Operations SET Cleaned = 1 WHERE Id = $id;", ("$id", id));

    /// <summary>Ends as cancelled the operations of another session that still run: the worker stopped while they did.</summary>
    /// <returns>How many.</returns>
    public int EndInterrupted()
        => Update(
            "UPDATE Operations SET State = 3, Ended = $now, Error = $error WHERE State = 0 AND Session <> $session;",
            ("$now", Format(DateTime.UtcNow)),
            ("$error", "Worker 没有正常结束。"),
            ("$session", Session));

    /// <summary>Gets the latest operations with the number of their records, the latest first.</summary>
    /// <param name="limit">How many at most.</param>
    /// <returns>The operations.</returns>
    public IReadOnlyList<OperationSummary> RecentOperations(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.Id, o.Kind, o.Started, o.Ended, o.State, o.Error, o.LibraryId, o.Location, o.EntryPath, o.LocalPath, o.Size, o.Sha256,
                o.JobId, o.LeaseToken, o.Directory, o.Cleaned,
                COUNT(u.Id), COALESCE(SUM(CASE WHEN u.Succeeded = 0 THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN u.Succeeded = 1 THEN u.Size ELSE 0 END), 0)
            FROM Operations o LEFT JOIN Uploads u ON u.OperationId = o.Id
            GROUP BY o.Id
            ORDER BY o.Started DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var operations = new List<OperationSummary>();
        while (reader.Read())
        {
            operations.Add(new OperationSummary(ReadOperation(reader), reader.GetInt32(16), reader.GetInt32(17), reader.GetInt64(18)));
        }

        return operations;
    }

    private static string Format(DateTime time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object Format(Guid? id) => id is { } value ? value.ToString("N", CultureInfo.InvariantCulture) : DBNull.Value;

    private static DateTime ParseTime(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static Guid? ReadGuid(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.ParseExact(reader.GetString(ordinal), "N");

    private static string? ReadString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static UploadOperation ReadOperation(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            (OperationKind)reader.GetInt32(1),
            ParseTime(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseTime(reader.GetString(3)),
            (OperationState)reader.GetInt32(4),
            ReadString(reader, 5),
            ReadGuid(reader, 6),
            ReadString(reader, 7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt64(10),
            ReadString(reader, 11),
            ReadGuid(reader, 12),
            ReadString(reader, 13),
            ReadString(reader, 14),
            reader.GetInt32(15) != 0);

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    // The tables of an earlier version get the new columns; the rows they had keep the defaults.
    private static void EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(Uploads);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        Execute(connection, "ALTER TABLE Uploads ADD COLUMN " + name + " " + definition + ";");
    }

    private int Update(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        return Execute(connection, null, sql, parameters);
    }

    private List<UploadRecord> ReadRecords(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var records = new List<UploadRecord>();
        while (reader.Read())
        {
            records.Add(new UploadRecord(
                ParseTime(reader.GetString(0)),
                (UploadKind)reader.GetInt32(1),
                ReadGuid(reader, 2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt32(6) != 0,
                ReadString(reader, 7),
                ReadString(reader, 8),
                ReadGuid(reader, 9),
                ReadString(reader, 10),
                (UploadState)reader.GetInt32(11)));
        }

        return records;
    }

    private List<UploadOperation> ReadOperations(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var operations = new List<UploadOperation>();
        while (reader.Read())
        {
            operations.Add(ReadOperation(reader));
        }

        return operations;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
