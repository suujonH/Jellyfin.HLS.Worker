using System.Net;

namespace Jellyfin.Hls.Worker.Protocol;

/// <summary>
/// A request the server refused, or that it did not answer.
/// </summary>
public sealed class ServerException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ServerException"/> class.
    /// </summary>
    public ServerException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ServerException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ServerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ServerException"/> class.
    /// </summary>
    /// <param name="statusCode">The status of the answer, or <c>null</c> when there was none.</param>
    /// <param name="message">The message.</param>
    public ServerException(HttpStatusCode? statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the status of the answer, or <c>null</c> when there was none.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Gets a value indicating whether the lease of the job is no longer valid.</summary>
    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;

    /// <summary>
    /// Gets whether an error is worth trying again: no answer, the network, too many requests, or an error of the server.
    /// </summary>
    /// <param name="exception">The error.</param>
    /// <returns>Whether it may pass.</returns>
    public static bool IsTransient(Exception exception) => exception switch
    {
        ServerException { StatusCode: null } => true,
        ServerException server => server.StatusCode == HttpStatusCode.TooManyRequests || (int)server.StatusCode!.Value >= 500,
        HttpRequestException or IOException => true,
        _ => false
    };
}
