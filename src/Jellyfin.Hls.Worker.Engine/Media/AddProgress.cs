namespace Jellyfin.Hls.Worker.Media;

/// <summary>A step of adding a file.</summary>
public enum AddStep
{
    /// <summary>The SHA-256 of a video is computed.</summary>
    Hash,

    /// <summary>The TrueCache entry of a video is made.</summary>
    Entry,

    /// <summary>The file is uploaded to a directory of the scheme.</summary>
    Upload,

    /// <summary>A file that is no video is written into the library.</summary>
    Library,

    /// <summary>The .strm file and its entry are written into the library.</summary>
    Shortcut,

    /// <summary>The video is probed and a job is asked for to encode it at once.</summary>
    Probe,

    /// <summary>The video qualities are encoded.</summary>
    EncodeVideo,

    /// <summary>The audio tracks are encoded.</summary>
    EncodeAudio,

    /// <summary>The subtitles are extracted.</summary>
    Subtitles,

    /// <summary>The preview is made.</summary>
    Preview,

    /// <summary>The renditions still being uploaded once all are encoded.</summary>
    UploadHls,

    /// <summary>The file is claimed, and what earlier additions of it left is resumed or removed.</summary>
    Prepare
}

/// <summary>What the progress of a step counts.</summary>
public enum ProgressUnit
{
    /// <summary>Bytes of the file.</summary>
    Bytes,

    /// <summary>Milliseconds of the media.</summary>
    Milliseconds,

    /// <summary>Files.</summary>
    Files
}

/// <summary>Something that happened while files are added.</summary>
public abstract record AddEvent;

/// <summary>The files of the addition, in the order they are added.</summary>
/// <param name="Files">Their paths in the library, with forward slashes.</param>
public sealed record AddFilesListed(IReadOnlyList<string> Files) : AddEvent;

/// <summary>A step of a file started.</summary>
/// <param name="File">The file, by its place in <see cref="AddFilesListed.Files"/>.</param>
/// <param name="Step">The step.</param>
/// <param name="Detail">What it works on, such as a directory or the qualities; may be empty.</param>
public sealed record AddStepStarted(int File, AddStep Step, string Detail) : AddEvent;

/// <summary>How far the current step of a file is.</summary>
/// <param name="File">The file, by its place in <see cref="AddFilesListed.Files"/>.</param>
/// <param name="Done">What is done.</param>
/// <param name="Total">What there is to do.</param>
/// <param name="Unit">What they count.</param>
public sealed record AddStepAdvanced(int File, long Done, long Total, ProgressUnit Unit) : AddEvent;

/// <summary>A file ended.</summary>
/// <param name="File">The file, by its place in <see cref="AddFilesListed.Files"/>.</param>
/// <param name="Added">Whether it was added.</param>
/// <param name="Message">A note when it was added, such as why it was not encoded; the error when it was not.</param>
public sealed record AddFileEnded(int File, bool Added, string? Message) : AddEvent;
