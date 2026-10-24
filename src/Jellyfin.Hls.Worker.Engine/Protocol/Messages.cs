namespace Jellyfin.Hls.Worker.Protocol;

// The messages of the HLS worker protocol, version 3, as the server defines them in MediaBrowser.Model.Hls.

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

/// <summary>
/// A request for a job: of the libraries given (<c>null</c> for any, empty for none), of none of the items excluded, and not only
/// of the qualities excluded for its library.
/// </summary>
public sealed record WorkerRequest(
    string Worker,
    Guid WorkerId,
    IReadOnlyList<Guid>? Libraries = null,
    IReadOnlyList<Guid>? ExcludedItems = null,
    IReadOnlyList<QualityFilter>? ExcludedQualities = null);

/// <summary>The video qualities the worker does not encode for a library.</summary>
public sealed record QualityFilter(Guid LibraryId, IReadOnlyList<int> Excluded);

/// <summary>A worker that asks for its id: the one it has (<c>null</c> for a new one) and its name.</summary>
public sealed record WorkerRegistration(Guid? Id, string Name);

/// <summary>The id the server knows a worker by.</summary>
public sealed record WorkerRegistered(Guid Id);

/// <summary>
/// A file to write into a folder of a library: the folder (<c>null</c> for the only one), the path in it with forward slashes,
/// its length and the worker.
/// </summary>
public sealed record LibraryUploadStart(string? Location, string Path, long Length, Guid WorkerId);

/// <summary>An upload into a library.</summary>
public sealed record LibraryUpload(string UploadId);

/// <summary>
/// The end of an upload into a library: the target of the .strm file, for the zip of a TrueCache entry, and the operation that
/// holds the file; when given, the file is put in place only while its working marker names this operation. The hash and size of
/// the media content the file is, of the target for a .strm file, let the server link the library item to the content at once.
/// </summary>
public sealed record LibraryUploadComplete(string? ShortcutTarget, string? OperationId = null, string? Sha256 = null, long? ContentLength = null);

/// <summary>
/// A file a worker adds to a folder of a library (<c>null</c> for the only one), by its path in it with forward slashes, marked as
/// held by one of its operations.
/// </summary>
public sealed record WorkingClaim(string? Location, string Path, string OperationId);

/// <summary>A file a worker adds to a folder of a library, by its path in it.</summary>
public sealed record WorkingPath(string? Location, string Path);

/// <summary>The mark of a file to remove while it names the operation.</summary>
public sealed record WorkingRelease(string? Location, string Path, string OperationId);

/// <summary>The operation that holds a file; <c>null</c> for none.</summary>
public sealed record WorkingState(string? OperationId);

/// <summary>A job handed to the worker.</summary>
public sealed record WorkAssignment(WorkLease Lease, SourceFile Source, string InputUrl);

/// <summary>The lease of a job.</summary>
public sealed record WorkLease(Guid JobId, string LeaseToken, int LeaseSeconds, int Attempt);

/// <summary>The library file a job reads.</summary>
public sealed record SourceFile(Guid ItemId, Guid LibraryId, string Path, long Length, DateTime DateModified);

/// <summary>What the worker found the input to be, and the qualities it does not encode for the library of the input.</summary>
public sealed record Identification(string LeaseToken, string Sha256, string ProbeJson, string Base, IReadOnlyList<int>? ExcludedQualities = null);

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

/// <summary>
/// A request to encode a content the worker adds; it names the job of an earlier addition of the file by this worker and its lease
/// token, to resume with its plan.
/// </summary>
public sealed record EagerRequest(
    string Worker,
    Guid WorkerId,
    Guid LibraryId,
    string Sha256,
    string ProbeJson,
    string Base,
    IReadOnlyList<int> Qualities,
    Guid? ResumeJobId = null,
    string? ResumeToken = null);

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
    int SarDenominator = 1,
    string ColorSpace = "");

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
