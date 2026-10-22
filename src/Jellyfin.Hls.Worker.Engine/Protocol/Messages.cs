namespace Jellyfin.Hls.Worker.Protocol;

// The messages of the HLS worker protocol, version 1, as the server defines them in MediaBrowser.Model.Hls.

/// <summary>What a rendition holds.</summary>
public enum RenditionKind
{
    /// <summary>A video quality.</summary>
    Video = 0,

    /// <summary>An audio track.</summary>
    Audio = 1,

    /// <summary>A subtitle track.</summary>
    Subtitle = 2,

    /// <summary>The preview image.</summary>
    Preview = 3
}

/// <summary>The protocol version of the server.</summary>
public sealed record ProtocolVersion(int Version);

/// <summary>A request that names the worker.</summary>
public sealed record WorkerRequest(string Worker);

/// <summary>A job handed to the worker.</summary>
public sealed record WorkAssignment(WorkLease Lease, SourceFile Source, string InputUrl);

/// <summary>The lease of a job.</summary>
public sealed record WorkLease(Guid JobId, string LeaseToken, int LeaseSeconds, int Attempt);

/// <summary>The library file a job reads.</summary>
public sealed record SourceFile(Guid ItemId, Guid LibraryId, string Path, long Length, DateTime DateModified);

/// <summary>What the worker found the input to be.</summary>
public sealed record Identification(string LeaseToken, string Sha256, string ProbeJson, string Base);

/// <summary>The answer to an identification: nothing left to do, or the plan.</summary>
public sealed record IdentifyResponse(bool Done, EncodePlan? Plan);

/// <summary>A request with the lease token.</summary>
public sealed record LeaseRequest(string LeaseToken);

/// <summary>A failed job.</summary>
public sealed record Failure(string LeaseToken, string Error);

/// <summary>A request that names an HLS root.</summary>
public sealed record BaseRequest(string Base);

/// <summary>The check of an HLS root.</summary>
public sealed record StoreTestResult(bool Success, bool HasMarker, string? Message);

/// <summary>A library that is pre-encoded, with its qualities.</summary>
public sealed record WorkerLibrary(Guid Id, string Name, IReadOnlyList<int> Qualities);

/// <summary>A request to encode a content the worker adds.</summary>
public sealed record EagerRequest(string Worker, Guid LibraryId, string Sha256, string ProbeJson, string Base, IReadOnlyList<int> Qualities);

/// <summary>The answer to an eager request.</summary>
public sealed record EagerResponse(bool Done, WorkLease? Lease, EncodePlan? Plan);

/// <summary>What the server plans to encode of a content.</summary>
public sealed record EncodePlan(
    string Sha256,
    string MediaKey,
    string Profile,
    Probe Probe,
    double SegmentSeconds,
    double KeyframeSeconds,
    int AudioSampleRate,
    int ExpectedSegments,
    IReadOnlyList<PlannedRendition> Renditions);

/// <summary>A rendition of a plan, written to its directory below the HLS root.</summary>
public sealed record PlannedRendition(string Key, RenditionKind Kind, string RelativeDirectory, VideoQuality? Video, int SourceIndex, int Channels);

/// <summary>A video quality.</summary>
public sealed record VideoQuality(int Name, int Height, int Width, double FramesPerSecond);

/// <summary>What the server read of the probe of a content.</summary>
public sealed record Probe(
    int Width,
    int Height,
    double FramesPerSecond,
    double DurationSeconds,
    string VideoCodec,
    string PixelFormat,
    string ColorTransfer,
    string FieldOrder,
    int Rotation,
    IReadOnlyList<AudioTrack> Audio,
    IReadOnlyList<SubtitleTrack> Subtitles,
    int VideoIndex = 0,
    int SarNumerator = 1,
    int SarDenominator = 1);

/// <summary>An audio track of a content.</summary>
public sealed record AudioTrack(int Index, string Codec, int Channels, int SampleRate, string Language, string Title, bool Default);

/// <summary>A subtitle track of a content.</summary>
public sealed record SubtitleTrack(int Index, string Codec, string Language, string Title, bool Default);

/// <summary>The renditions a job wrote.</summary>
public sealed record Completion(string LeaseToken, IReadOnlyList<RenditionOutput> Renditions);

/// <summary>A rendition that was written.</summary>
public sealed record RenditionOutput(string Key, RenditionSummary Summary, IReadOnlyList<OutputFile> Files, IReadOnlyList<double> SegmentDurations);

/// <summary>A file of a rendition, relative to its directory.</summary>
public sealed record OutputFile(string Path, long Length);

/// <summary>What a worker reports about a rendition.</summary>
public sealed record RenditionSummary(
    int Quality = 0,
    int Width = 0,
    int Height = 0,
    double FramesPerSecond = 0,
    int Bandwidth = 0,
    int AverageBandwidth = 0,
    string Codec = "",
    int SourceIndex = -1,
    string Language = "",
    string Title = "",
    int Channels = 0,
    bool Default = false);

/// <summary>A library of the server with its folders, as <c>GET /Library/VirtualFolders</c> returns it.</summary>
public sealed record VirtualFolder(string Name, string ItemId, IReadOnlyList<string> Locations);

/// <summary>An access of the network storage of the server; secrets are not returned.</summary>
public sealed record NetworkStorageAccess(
    Guid Id,
    string Protocol,
    string? Name,
    string? Endpoint,
    string Bucket,
    string? Region,
    bool UsePathStyle,
    string? AccessKey,
    string? SecretKey,
    bool IsArchive);
