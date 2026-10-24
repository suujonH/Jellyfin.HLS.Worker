using System.Globalization;
using System.Text.Json;
using Jellyfin.Hls.Worker.Protocol;

namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>
/// The outputs a job of a file being added keeps in the directory of its operation: plan.json, the plan they were encoded for, and
/// encoded.json, the keys of the renditions encoded to their end. A rendition is kept for a new plan of the same content when its
/// spec is the same, whatever its directory on the server.
/// </summary>
internal static class KeptOutputs
{
    private const string PlanFile = "plan.json";
    private const string EncodedFile = "encoded.json";

    /// <summary>
    /// Arranges the outputs of an earlier encoding for a plan: those it keeps are moved to the directories of their renditions in
    /// <c>output</c>, the others are deleted.
    /// </summary>
    /// <param name="directory">The directory of the operation.</param>
    /// <param name="plan">The plan.</param>
    /// <returns>The keys of the renditions kept.</returns>
    public static HashSet<string> Load(string directory, EncodePlan plan)
    {
        var output = Path.Combine(directory, "output");
        var kept = new HashSet<string>(StringComparer.Ordinal);
        var old = Read<EncodePlan>(Path.Combine(directory, PlanFile));
        var encoded = Read<List<string>>(Path.Combine(directory, EncodedFile));
        if (old is null || encoded is null || !Compatible(old, plan))
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, true);
            }

            Write(directory, plan, kept);
            return kept;
        }

        var previous = Path.Combine(directory, "output.previous");
        if (Directory.Exists(previous))
        {
            Directory.Delete(previous, true);
        }

        if (Directory.Exists(output))
        {
            Directory.Move(output, previous);
        }

        Directory.CreateDirectory(output);
        for (var i = 0; i < plan.Renditions.Count; i++)
        {
            var rendition = plan.Renditions[i];
            var j = IndexOf(old.Renditions, rendition.Key);
            var from = Path.Combine(previous, j.ToString("D3", CultureInfo.InvariantCulture));
            if (j >= 0 && encoded.Contains(rendition.Key, StringComparer.Ordinal) && SameSpec(old.Renditions[j], rendition) && Directory.Exists(from))
            {
                Directory.Move(from, Path.Combine(output, i.ToString("D3", CultureInfo.InvariantCulture)));
                kept.Add(rendition.Key);
            }
        }

        if (Directory.Exists(previous))
        {
            Directory.Delete(previous, true);
        }

        Write(directory, plan, kept);
        return kept;
    }

    /// <summary>
    /// Records renditions encoded to their end.
    /// </summary>
    /// <param name="directory">The directory of the operation.</param>
    /// <param name="keys">The keys of the renditions.</param>
    public static void MarkEncoded(string directory, IEnumerable<string> keys)
    {
        var encoded = new SortedSet<string>(Read<List<string>>(Path.Combine(directory, EncodedFile)) ?? [], StringComparer.Ordinal);
        encoded.UnionWith(keys);
        WriteAtomically(Path.Combine(directory, EncodedFile), JsonSerializer.Serialize(encoded.ToList(), ServerClient.Json));
    }

    // The outputs of one plan fit another of the same content, profile and segments.
    private static bool Compatible(EncodePlan a, EncodePlan b)
        => string.Equals(a.Sha256, b.Sha256, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Profile, b.Profile, StringComparison.Ordinal)
            && a.SegmentSeconds.Equals(b.SegmentSeconds)
            && a.KeyframeSeconds.Equals(b.KeyframeSeconds)
            && a.AudioSampleRate == b.AudioSampleRate
            && a.ExpectedSegments == b.ExpectedSegments;

    private static bool SameSpec(PlannedRendition x, PlannedRendition y)
        => x.Kind == y.Kind && Equals(x.Video, y.Video) && x.SourceIndex == y.SourceIndex && x.Channels == y.Channels;

    private static int IndexOf(IReadOnlyList<PlannedRendition> renditions, string key)
    {
        for (var i = 0; i < renditions.Count; i++)
        {
            if (string.Equals(renditions[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // A file that is missing or cannot be read is as none.
    private static T? Read<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), ServerClient.Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Write(string directory, EncodePlan plan, HashSet<string> kept)
    {
        WriteAtomically(Path.Combine(directory, PlanFile), JsonSerializer.Serialize(plan, ServerClient.Json));
        WriteAtomically(Path.Combine(directory, EncodedFile), JsonSerializer.Serialize(kept.Order(StringComparer.Ordinal).ToList(), ServerClient.Json));
    }

    private static void WriteAtomically(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, true);
    }
}
