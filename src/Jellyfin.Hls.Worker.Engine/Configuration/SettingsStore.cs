using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Hls.Worker.Jobs;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Hls.Worker.Configuration;

/// <summary>
/// Keeps the settings in the database of the worker, as one row of JSON. The settings file of former versions is imported the
/// first time, when the database has none.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsStore"/> class, creating the database if needed.
    /// </summary>
    /// <param name="paths">Where the worker keeps its data.</param>
    public SettingsStore(WorkerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Paths = paths;
        Directory.CreateDirectory(paths.DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS Settings (Id INTEGER PRIMARY KEY CHECK (Id = 1), Json TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    /// <summary>Gets where the worker keeps its data.</summary>
    public WorkerPaths Paths { get; }

    /// <summary>
    /// Reads the settings as they are stored, to be edited and written again: relative paths are kept, the API key of the
    /// environment is not used, and nothing is checked. A scheme a library refers to that is gone is no default.
    /// </summary>
    /// <returns>The settings; the defaults when none are stored.</returns>
    /// <exception cref="ArgumentException">The settings stored are not valid JSON.</exception>
    public WorkerSettings Read() => Read(out _);

    /// <summary>
    /// Reads the settings as <see cref="Read()"/> does, and tells whether they were imported from the settings file of former versions.
    /// </summary>
    /// <param name="imported">Whether the settings were imported now.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentException">The settings stored are not valid JSON.</exception>
    public WorkerSettings Read(out bool imported)
    {
        imported = false;
        string? json;
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Json FROM Settings WHERE Id = 1;";
            json = command.ExecuteScalar() as string;
        }

        WorkerSettings settings;
        if (json is not null)
        {
            try
            {
                settings = JsonSerializer.Deserialize<WorkerSettings>(Upgrade(json), WorkerSettings.Json) ?? new WorkerSettings();
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("配置无效：" + ex.Message, ex);
            }

            WorkerSettings.Normalize(settings);
        }
        else if (File.Exists(Paths.LegacySettings))
        {
            settings = ImportLegacy(Paths.LegacySettings);
            WorkerSettings.Normalize(settings);
            Write(settings);
            imported = true;
        }
        else
        {
            return new WorkerSettings();
        }

        foreach (var library in settings.Libraries.Where(l => l.SchemeId is not null && settings.GetScheme(l.SchemeId) is null))
        {
            library.SchemeId = null;
        }

        return settings;
    }

    // Settings of former versions of the store: a library was encoded in every quality at once, or in none; and see UpgradeSchemes.
    private static string Upgrade(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            return json;
        }

        var changed = false;
        foreach (var library in (root["Libraries"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            if (!library.TryGetPropertyValue("PreEncode", out var old))
            {
                continue;
            }

            library.Remove("PreEncode");
            changed = true;
            var qualities = new JsonArray();
            if (old is JsonValue value
                && value.TryGetValue<bool>(out var on)
                && on
                && root["PreEncodes"] is JsonArray preEncodes
                && preEncodes.OfType<JsonObject>().FirstOrDefault(p => string.Equals((string?)p["LibraryId"], (string?)library["Id"], StringComparison.OrdinalIgnoreCase)) is { } preEncode
                && preEncode["Qualities"] is JsonArray known)
            {
                foreach (var quality in known)
                {
                    qualities.Add(quality?.DeepClone());
                }
            }

            library["PreEncodeQualities"] = qualities;
        }

        changed |= UpgradeSchemes(root);
        return changed ? root.ToJsonString() : json;
    }

    // Settings of the versions whose lines of a scheme had a path below the connection, and a declaration of their own: each
    // directory below a connection becomes a connection of its own, and only the first line declared stays declared.
    private static bool UpgradeSchemes(JsonObject root)
    {
        if (root["Schemes"] is not JsonArray schemes
            || !schemes.OfType<JsonObject>()
                .SelectMany(s => Lines(s, "Hls").Concat(Lines(s, "Sources")))
                .Any(l => l.ContainsKey("Upload") || l.ContainsKey("DeclareSameAsUpload") || l.ContainsKey("Declared")))
        {
            return false;
        }

        if (root["Connections"] is not JsonArray connections)
        {
            connections = new JsonArray();
            root["Connections"] = connections;
        }

        var names = new HashSet<string>(connections.OfType<JsonObject>().Select(c => (string?)c["Name"] ?? string.Empty), StringComparer.Ordinal);
        var folded = new Dictionary<(Guid, string), Guid>();
        JsonObject? Find(Guid id) => connections.OfType<JsonObject>().FirstOrDefault(c => Guid.TryParse((string?)c["Id"], out var other) && other.Equals(id));
        bool IsServer(Guid id) => Find(id)?["Type"] is JsonValue type
            && ((type.TryGetValue<string>(out var text) && text.Equals(nameof(ConnectionType.Server), StringComparison.OrdinalIgnoreCase))
                || (type.TryGetValue<int>(out var number) && number == (int)ConnectionType.Server));

        // The connection of a target, a new one for a directory below it.
        Guid? Fold(JsonNode? node)
        {
            if (node is not JsonObject target || !Guid.TryParse((string?)target["ConnectionId"], out var id))
            {
                return null;
            }

            var path = ((string?)target["Path"] ?? string.Empty).Trim('/');
            if (path.Length == 0 || id.Equals(SchemeEntry.JellyfinLibrary) || Find(id) is not { } connection)
            {
                return id;
            }

            if (!folded.TryGetValue((id, path), out var below))
            {
                var settings = JsonSerializer.Deserialize<ConnectionSettings>(connection.ToJsonString(), WorkerSettings.Json)!;
                var copy = settings.Below(path, Guid.NewGuid(), UniqueName(names, settings.Name + "/" + path));
                connections.Add(JsonSerializer.SerializeToNode(copy, WorkerSettings.Json));
                below = folded[(id, path)] = copy.Id;
            }

            return below;
        }

        JsonArray Upgrade(JsonObject scheme, string key, bool hls)
        {
            var lines = new JsonArray();
            var declared = false;
            void Add(Guid id, bool declare)
            {
                lines.Add(new JsonObject { ["ConnectionId"] = id.ToString(), ["Declare"] = declare });
                declared |= declare;
            }

            foreach (var line in Lines(scheme, key))
            {
                var upload = Fold(line["Upload"]);
                var same = line["DeclareSameAsUpload"] is not JsonValue value || !value.TryGetValue<bool>(out var on) || on;
                var elsewhere = same ? null : line["Declared"] as JsonObject;
                if (upload is { } target)
                {
                    Add(target, (same || target.Equals(SchemeEntry.JellyfinLibrary)) && !declared);

                    // A directory of the server the file reaches by another way; any other declaration is no longer possible.
                    if (!declared
                        && Guid.TryParse((string?)elsewhere?["ConnectionId"], out var server)
                        && IsServer(server)
                        && Fold(elsewhere) is { } below)
                    {
                        Add(below, true);
                    }
                }
                else if (!hls && Fold(elsewhere) is { } only)
                {
                    Add(only, !declared);
                }
            }

            return lines;
        }

        foreach (var scheme in schemes.OfType<JsonObject>())
        {
            scheme["Hls"] = Upgrade(scheme, "Hls", true);
            scheme["Sources"] = Upgrade(scheme, "Sources", false);
        }

        return true;
    }

    private static List<JsonObject> Lines(JsonObject scheme, string key)
        => scheme[key] is JsonArray lines ? lines.OfType<JsonObject>().ToList() : [];

    // A name of a connection not taken yet, of 64 characters at most.
    private static string UniqueName(HashSet<string> names, string name)
    {
        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? string.Empty : " " + i.ToString(CultureInfo.InvariantCulture);
            var candidate = (name.Length + suffix.Length > 64 ? name[..(64 - suffix.Length)] : name) + suffix;
            if (names.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Writes the settings.
    /// </summary>
    /// <param name="settings">The settings.</param>
    public void Write(WorkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings (Id, Json) VALUES (1, $json) ON CONFLICT (Id) DO UPDATE SET Json = excluded.Json;";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, WorkerSettings.Json));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads and validates the settings for a run: the API key of the environment takes precedence, and relative paths are
    /// relative to the data directory.
    /// </summary>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentException">The settings are not valid.</exception>
    public WorkerSettings Load()
    {
        var settings = Read();
        if (Environment.GetEnvironmentVariable("JELLYFIN_HLS_API_KEY") is { Length: > 0 } apiKey)
        {
            settings.ApiKey = apiKey;
        }

        Resolve(settings);
        settings.Validate();
        return settings;
    }

    /// <summary>
    /// Resolves the paths of a connection as <see cref="Load"/> does: relative to the data directory.
    /// </summary>
    /// <param name="connection">The connection.</param>
    public void Resolve(ConnectionSettings connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Type == ConnectionType.Worker && !string.IsNullOrWhiteSpace(connection.LocalRoot))
        {
            connection.LocalRoot = Path.GetFullPath(connection.LocalRoot, Paths.DataDirectory);
        }

        if (connection.Type == ConnectionType.Sftp && !string.IsNullOrWhiteSpace(connection.PrivateKeyPath))
        {
            connection.PrivateKeyPath = Path.GetFullPath(connection.PrivateKeyPath, Paths.DataDirectory);
        }
    }

    /// <summary>
    /// Records the id the server gave the worker, leaving the rest of the settings as they are stored.
    /// </summary>
    /// <param name="id">The id.</param>
    public void SetWorkerId(Guid id)
    {
        var settings = Read();
        settings.WorkerId = id;
        Write(settings);
    }

    private void Resolve(WorkerSettings settings)
    {
        settings.TempDirectory = settings.TempDirectory.Length == 0
            ? Paths.DefaultTempDirectory
            : Path.GetFullPath(settings.TempDirectory, Paths.DataDirectory);
        settings.Connections.ForEach(Resolve);
    }

    // The file of former versions is read field by field: it has fields that are gone. Its connections become the remote
    // directories, its HLS root a scheme; the defaults of its libraries named connections that no longer mean the same.
    private WorkerSettings ImportLegacy(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new ArgumentException("worker.json 不是对象。");
        var settings = new WorkerSettings();
        settings.Server = Text(root, "Server") ?? settings.Server;
        settings.ApiKey = Text(root, "ApiKey") ?? settings.ApiKey;
        settings.Name = Text(root, "Name") ?? settings.Name;
        settings.Encoder = Text(root, "Encoder") ?? settings.Encoder;
        settings.HardwareDevice = Text(root, "HardwareDevice") ?? settings.HardwareDevice;
        settings.Threads = Number(root, "Threads") ?? settings.Threads;
        settings.Concurrency = Number(root, "Concurrency") ?? settings.Concurrency;
        settings.UploadConcurrency = Number(root, "UploadConcurrency") ?? settings.UploadConcurrency;
        settings.PollSeconds = Number(root, "PollSeconds") ?? settings.PollSeconds;
        settings.ControlTimeoutSeconds = Number(root, "ControlTimeoutSeconds") ?? settings.ControlTimeoutSeconds;
        settings.InputCacheGiB = Get(root, "InputCacheGiB") is JsonValue cache && cache.TryGetValue<double>(out var gib) ? gib : settings.InputCacheGiB;
        settings.KeepFailedJobs = Get(root, "KeepFailedJobs") is JsonValue keep && keep.TryGetValue<bool>(out var kept) ? kept : settings.KeepFailedJobs;
        var work = Text(root, "WorkDirectory") ?? "work";
        settings.TempDirectory = work is "" or "work" ? string.Empty : Path.GetFullPath(work, directory);

        var names = new Dictionary<string, ConnectionSettings>(StringComparer.Ordinal);
        foreach (var node in (Get(root, "Connections") as JsonArray ?? []).OfType<JsonObject>())
        {
            var connection = ImportConnection(node, directory);
            if (connection is not null && names.TryAdd(connection.Name, connection))
            {
                settings.Connections.Add(connection);
            }
        }

        if (Get(root, "Hls") is JsonObject hls && Text(hls, "Connection") is { } name && names.TryGetValue(name, out var target))
        {
            // A directory below the connection is a connection of its own.
            if ((Text(hls, "Path") ?? string.Empty).Trim('/') is { Length: > 0 } below)
            {
                target = target.Below(below, Guid.NewGuid(), UniqueName([.. names.Keys], target.Name + "/" + below));
                settings.Connections.Add(target);
            }

            settings.Schemes.Add(new SchemeSettings
            {
                Id = Guid.NewGuid(),
                Name = "导入的方案",
                Hls = [new SchemeEntry { ConnectionId = target.Id, Declare = true }]
            });
        }

        ImportMedia(Path.Combine(work.Length == 0 ? Path.Combine(directory, "work") : Path.GetFullPath(work, directory), "worker.db"));
        return settings;
    }

    private static ConnectionSettings? ImportConnection(JsonObject node, string directory)
    {
        var type = Get(node, "Type") switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text.ToUpperInvariant() switch
            {
                "S3" => ConnectionType.S3,
                "SFTP" => ConnectionType.Sftp,
                "LOCAL" => ConnectionType.Worker,
                _ => (ConnectionType?)null
            },
            JsonValue value when value.TryGetValue<int>(out var number) => number switch
            {
                0 => ConnectionType.S3,
                1 => ConnectionType.Sftp,
                2 => ConnectionType.Worker,
                _ => null
            },
            _ => null
        };
        if (type is null || Text(node, "Name") is not { Length: > 0 } name)
        {
            return null;
        }

        var connection = new ConnectionSettings { Id = Guid.NewGuid(), Name = name, Type = type.Value };
        switch (type)
        {
            case ConnectionType.S3:
                connection.Endpoint = Text(node, "Endpoint") ?? connection.Endpoint;
                connection.Region = Text(node, "Region") ?? connection.Region;
                connection.Bucket = Text(node, "Bucket") ?? connection.Bucket;
                connection.BucketPath = Text(node, "BucketPath") ?? connection.BucketPath;
                connection.AccessKey = Text(node, "AccessKey") ?? connection.AccessKey;
                connection.SecretKey = Text(node, "SecretKey") ?? connection.SecretKey;
                connection.PartConcurrency = Number(node, "PartConcurrency") ?? connection.PartConcurrency;
                break;
            case ConnectionType.Sftp:
                connection.Host = Text(node, "Host") ?? connection.Host;
                connection.Port = Number(node, "Port") ?? connection.Port;
                connection.HostKey = Text(node, "HostKey") ?? connection.HostKey;
                connection.User = Text(node, "User") ?? connection.User;
                connection.Password = Text(node, "Password") ?? connection.Password;
                connection.PrivateKeyPath = Text(node, "PrivateKeyPath") is { Length: > 0 } key ? Path.GetFullPath(key, directory) : string.Empty;
                connection.PrivateKeyPassphrase = Text(node, "PrivateKeyPassphrase") ?? connection.PrivateKeyPassphrase;
                connection.RemoteRoot = Text(node, "RemoteRoot") ?? connection.RemoteRoot;
                break;
            default:
                connection.LocalRoot = Text(node, "LocalRoot") is { Length: > 0 } local ? Path.GetFullPath(local, directory) : string.Empty;
                break;
        }

        return connection;
    }

    // The media the former version added, so their jobs still read the local files.
    private void ImportMedia(string database)
    {
        if (!File.Exists(database))
        {
            return;
        }

        _ = new MediaDatabase(Paths.Database);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $old AS old; INSERT OR IGNORE INTO Media SELECT * FROM old.Media; DETACH DATABASE old;";
        command.Parameters.AddWithValue("$old", database);
        command.ExecuteNonQuery();
    }

    // The former version read the names without case.
    private static JsonNode? Get(JsonObject node, string name)
        => node.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(JsonObject node, string name)
        => Get(node, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonObject node, string name)
        => Get(node, name) is JsonValue value && value.TryGetValue<int>(out var number) ? number
            : Get(node, name) is JsonValue text && text.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : null;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
