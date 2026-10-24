using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Jellyfin.Hls.Worker.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// An S3 compatible bucket. The client is shared by every session; S3 has no sessions of its own.
/// </summary>
internal sealed class S3Connection : StorageConnection
{
    // A file up to this size is put at once; a larger one in parts of this size, or larger ones when it would take more than
    // MaxParts of them. These are the limits AWS S3, Cloudflare R2 and Backblaze B2 share.
    private const long MultipartThreshold = 64L << 20;
    private const int MaxParts = 10_000;
    private const long MaxPartSize = 5L << 30;

    // A part that fails for a passing reason is sent again after 2, 4, 8, 16 and 32 seconds, beyond the retries of the SDK.
    private const int PartAttempts = 6;

    private readonly AmazonS3Client _client;
    private readonly ILogger _logger;

    public S3Connection(ConnectionSettings settings, ILogger logger)
        : base(settings)
    {
        _logger = logger;
        // Path style and checksums only where required: what MinIO, Ceph, Garage and the other S3 services all understand.
        _client = new AmazonS3Client(
            new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = settings.Endpoint,
                AuthenticationRegion = settings.Region,
                ForcePathStyle = true,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            });
    }

    public override Task<IStorageSession> OpenAsync(CancellationToken cancellationToken)
        => Task.FromResult<IStorageSession>(new Session(_client, Settings, _logger));

    /// <summary>
    /// Lists the bucket, as the server's test does: a missing bucket answers a HEAD of a file with the 404 of a missing file.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">The bucket does not exist.</exception>
    public override async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Settings.Bucket, MaxKeys = 1 }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
        {
            throw new InvalidOperationException("连接 " + Settings.Name + " 的桶 " + Settings.Bucket + " 不存在（桶名区分大小写）。", ex);
        }
    }

    /// <summary>
    /// Gets the size of the parts of a file put in parts: all are of this size but the last, which R2 and B2 require. It is
    /// <see cref="MultipartThreshold"/>, or the smallest whole MiB that needs no more than <see cref="MaxParts"/> parts.
    /// </summary>
    /// <param name="length">The length of the file.</param>
    /// <returns>The size of a part.</returns>
    /// <exception cref="InvalidDataException">The file needs parts larger than S3 takes.</exception>
    internal static long GetPartSize(long length)
    {
        const long mebibyte = 1L << 20;
        var needed = (length + MaxParts - 1) / MaxParts;
        var size = Math.Max(MultipartThreshold, (needed + mebibyte - 1) / mebibyte * mebibyte);
        return size <= MaxPartSize
            ? size
            : throw new InvalidDataException("文件长 " + length + " 字节，超过 S3 分段上传的上限（" + MaxParts + " 个 5 GiB 的分段）。");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Dispose();
        }

        base.Dispose(disposing);
    }

    // The paths of a session are below the BucketPath of the connection.
    private sealed class Session(AmazonS3Client client, ConnectionSettings settings, ILogger logger) : IStorageSession
    {
        public async Task PutAsync(string path, string localFile, CancellationToken cancellationToken, Action<long>? sent = null)
        {
            // A single PUT is limited to 5 GiB; a media file is uploaded in parts, each checked by the service on its own.
            var key = settings.GetS3Key(path);
            var length = new FileInfo(localFile).Length;
            if (length > MultipartThreshold)
            {
                await PutPartsAsync(key, localFile, length, sent, cancellationToken).ConfigureAwait(false);
                return;
            }

            var digest = await HashAsync(localFile, 0, length, cancellationToken).ConfigureAwait(false);

            // A PUT is atomic in S3, and Content-MD5 makes the service refuse a body that was damaged on the way.
            var request = new PutObjectRequest
            {
                BucketName = settings.Bucket,
                Key = key,
                FilePath = localFile,
                ContentType = GetContentType(key),
                MD5Digest = digest,
                UseChunkEncoding = false
            };
            if (sent is not null)
            {
                request.StreamTransferProgress += (_, e) => sent(e.TransferredBytes);
            }

            await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Content-MD5 is what S3 defines for the integrity of a body; it is no protection against an attacker.
        private static async Task<string> HashAsync(string localFile, long offset, long count, CancellationToken cancellationToken)
        {
            var stream = File.OpenRead(localFile);
            await using (stream.ConfigureAwait(false))
            {
                stream.Position = offset;
#pragma warning disable CA5351
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351
                var buffer = new byte[1 << 20];
                while (count > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("文件在上传期间变短：" + localFile);
                    }

                    md5.AppendData(buffer, 0, read);
                    count -= read;
                }

                return Convert.ToBase64String(md5.GetHashAndReset());
            }
        }

        // Written by hand rather than with TransferUtility, whose parts are sent with aws-chunked encoding that some S3 services
        // refuse. The object appears when the upload completes; one that fails is aborted, so no part is left stored.
        // PartConcurrency parts are uploaded at a time, in order: as one is done, the next one starts. A part that fails for a
        // passing reason is sent again on its own; one that fails for good stops the others, and the upload with them.
        private async Task PutPartsAsync(string path, string localFile, long length, Action<long>? sent, CancellationToken cancellationToken)
        {
            // The bytes of every part, as far as each attempt of it got.
            long total = 0;
            Action<long>? add = sent is null ? null : delta => sent(Interlocked.Add(ref total, delta));
            var partSize = GetPartSize(length);
            var parts = new PartETag[(int)((length + partSize - 1) / partSize)];
            var upload = await client.InitiateMultipartUploadAsync(
                new InitiateMultipartUploadRequest { BucketName = settings.Bucket, Key = path, ContentType = GetContentType(path) },
                cancellationToken).ConfigureAwait(false);
            try
            {
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, parts.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = settings.PartConcurrency, CancellationToken = cancellationToken },
                    async (index, token) =>
                    {
                        var offset = index * partSize;
                        var size = Math.Min(partSize, length - offset);
                        parts[index] = await PutPartAsync(path, localFile, upload.UploadId, index + 1, offset, size, add, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                await client.CompleteMultipartUploadAsync(
                    new CompleteMultipartUploadRequest { BucketName = settings.Bucket, Key = path, UploadId = upload.UploadId, PartETags = [.. parts] },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await client.AbortMultipartUploadAsync(settings.Bucket, path, upload.UploadId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Left to the lifecycle rules of the bucket.
                }

                throw;
            }
        }

        private async Task<PartETag> PutPartAsync(
            string path,
            string localFile,
            string uploadId,
            int number,
            long offset,
            long size,
            Action<long>? add,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                long attemptSent = 0;
                try
                {
                    var request = new UploadPartRequest
                    {
                        BucketName = settings.Bucket,
                        Key = path,
                        UploadId = uploadId,
                        PartNumber = number,
                        FilePath = localFile,
                        FilePosition = offset,
                        PartSize = size,
                        MD5Digest = await HashAsync(localFile, offset, size, cancellationToken).ConfigureAwait(false),
                        UseChunkEncoding = false
                    };
                    if (add is not null)
                    {
                        request.StreamTransferProgress += (_, e) =>
                        {
                            var delta = e.TransferredBytes - attemptSent;
                            attemptSent = e.TransferredBytes;
                            add(delta);
                        };
                    }

                    var part = await client.UploadPartAsync(request, cancellationToken).ConfigureAwait(false);
                    return new PartETag(number, part.ETag);
                }
                catch (Exception ex) when (attempt < PartAttempts && !cancellationToken.IsCancellationRequested && IsTransient(ex))
                {
                    // What this attempt sent is sent again.
                    add?.Invoke(-attemptSent);
                    var delay = TimeSpan.FromSeconds(1 << attempt);
                    logger.LogWarning(
                        "上传 {Path} 的第 {Part} 段暂时失败，{Delay} 秒后第 {Attempt} 次重试：{Message}",
                        path,
                        number,
                        delay.TotalSeconds,
                        attempt,
                        ex.Message);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                await client.GetObjectMetadataAsync(settings.Bucket, settings.GetS3Key(path), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
            => client.DeleteObjectAsync(settings.Bucket, settings.GetS3Key(path), cancellationToken);

        public Task<bool> MoveDirectoryAsync(string path, string newPath, CancellationToken cancellationToken)
            => throw new NotSupportedException("S3 没有目录。");

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
            => throw new NotSupportedException("S3 没有目录。");

        public void Dispose()
        {
        }
    }
}
