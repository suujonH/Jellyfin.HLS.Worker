using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>
/// A local copy of a library file: where it is on this machine and its SHA-256.
/// </summary>
/// <param name="LocalPath">The file on this machine.</param>
/// <param name="Sha256">Its SHA-256, in lower-case hexadecimal.</param>
public sealed record LocalCopy(string LocalPath, string Sha256);

/// <summary>
/// The library files this worker has a copy of: the media it added. Downloaded inputs are not recorded here, as the input cache
/// trims them while they are not leased from it. A file is known by its target,
/// as the server names it in a job (an <c>s3://</c> address for media on S3, a path of the server otherwise), and its size.
/// The modified time the server gives the file is not known when it is added; the one of its first job is kept, and a later job
/// with another one is of a file replaced since, so the copy is forgotten.
/// </summary>
public sealed class MediaDatabase
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaDatabase"/> class, creating the database if needed.
    /// </summary>
    /// <param name="path">The database file.</param>
    public MediaDatabase(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Media (
                Target TEXT NOT NULL,
                Size INTEGER NOT NULL,
                LocalPath TEXT NOT NULL,
                Sha256 TEXT NOT NULL,
                LocalTicks INTEGER NOT NULL,
                DateModified TEXT NOT NULL,
                SourceTicks INTEGER NULL,
                PRIMARY KEY (Target, Size));
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records a copy, replacing the one of the same target and size; the modified time of the server is learned again.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="size">The size.</param>
    /// <param name="copy">The copy.</param>
    public void Record(string target, long size, LocalCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Media (Target, Size, LocalPath, Sha256, LocalTicks, DateModified) VALUES ($target, $size, $path, $sha, $ticks, $date)
            ON CONFLICT (Target, Size) DO UPDATE SET LocalPath = excluded.LocalPath, Sha256 = excluded.Sha256, LocalTicks = excluded.LocalTicks,
                DateModified = excluded.DateModified, SourceTicks = NULL;
            """;
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$path", copy.LocalPath);
        command.Parameters.AddWithValue("$sha", copy.Sha256);
        command.Parameters.AddWithValue("$ticks", File.GetLastWriteTimeUtc(copy.LocalPath).Ticks);
        command.Parameters.AddWithValue("$date", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Finds the copy of a file that is still there, unchanged since it was recorded, and of the file the server has, or forgets it.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="size">The size.</param>
    /// <param name="sourceModified">The modified time the server gives the file.</param>
    /// <returns>The copy, or <c>null</c>.</returns>
    public LocalCopy? Find(string target, long size, DateTime sourceModified)
    {
        LocalCopy? copy = null;
        long ticks = 0;
        long? sourceTicks = null;
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT LocalPath, Sha256, LocalTicks, SourceTicks FROM Media WHERE Target = $target AND Size = $size;";
            command.Parameters.AddWithValue("$target", target);
            command.Parameters.AddWithValue("$size", size);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                copy = new LocalCopy(reader.GetString(0), reader.GetString(1));
                ticks = reader.GetInt64(2);
                sourceTicks = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            }
        }

        if (copy is null)
        {
            return null;
        }

        var file = new FileInfo(copy.LocalPath);
        var source = sourceModified.ToUniversalTime().Ticks;
        if (file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == ticks && (sourceTicks ?? source) == source)
        {
            if (sourceTicks is null)
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Media SET SourceTicks = $source WHERE Target = $target AND Size = $size;";
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$target", target);
                command.Parameters.AddWithValue("$size", size);
                command.ExecuteNonQuery();
            }

            return copy;
        }

        Forget(target, size);
        return null;
    }

    /// <summary>
    /// Forgets the copy of a file.
    /// </summary>
    /// <param name="target">The target.</param>
    /// <param name="size">The size.</param>
    public void Forget(string target, long size)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Media WHERE Target = $target AND Size = $size;";
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$size", size);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
