using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Jellyfin.Hls.Worker.Configuration;

namespace Jellyfin.Hls.Worker.Storage;

/// <summary>
/// An S3 compatible bucket. The client is shared by every session; S3 has no sessions of its own.
/// </summary>
internal sealed class S3Connection : StorageConnection
{
    private const long MultipartThreshold = 64L << 20;

    private readonly AmazonS3Client _client;

    public S3Connection(ConnectionSettings settings)
        : base(settings)
    {
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
        => Task.FromResult<IStorageSession>(new Session(_client, Settings.Bucket));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class Session(AmazonS3Client client, string bucket) : IStorageSession
    {
        public async Task PutAsync(string path, string localFile, CancellationToken cancellationToken)
        {
            // A single PUT is limited to 5 GiB; a media file is uploaded in parts, each checked by the service on its own.
            var length = new FileInfo(localFile).Length;
            if (length > MultipartThreshold)
            {
                await PutPartsAsync(path, localFile, length, cancellationToken).ConfigureAwait(false);
                return;
            }

            var digest = await HashAsync(localFile, 0, length, cancellationToken).ConfigureAwait(false);

            // A PUT is atomic in S3, and Content-MD5 makes the service refuse a body that was damaged on the way.
            await client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = path,
                    FilePath = localFile,
                    ContentType = GetContentType(path),
                    MD5Digest = digest,
                    UseChunkEncoding = false
                },
                cancellationToken).ConfigureAwait(false);
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
        private async Task PutPartsAsync(string path, string localFile, long length, CancellationToken cancellationToken)
        {
            var upload = await client.InitiateMultipartUploadAsync(
                new InitiateMultipartUploadRequest { BucketName = bucket, Key = path, ContentType = GetContentType(path) },
                cancellationToken).ConfigureAwait(false);
            try
            {
                var parts = new List<PartETag>();
                for (long offset = 0; offset < length; offset += MultipartThreshold)
                {
                    var size = Math.Min(MultipartThreshold, length - offset);
                    var part = await client.UploadPartAsync(
                        new UploadPartRequest
                        {
                            BucketName = bucket,
                            Key = path,
                            UploadId = upload.UploadId,
                            PartNumber = parts.Count + 1,
                            FilePath = localFile,
                            FilePosition = offset,
                            PartSize = size,
                            MD5Digest = await HashAsync(localFile, offset, size, cancellationToken).ConfigureAwait(false),
                            UseChunkEncoding = false
                        },
                        cancellationToken).ConfigureAwait(false);
                    parts.Add(new PartETag(parts.Count + 1, part.ETag));
                }

                await client.CompleteMultipartUploadAsync(
                    new CompleteMultipartUploadRequest { BucketName = bucket, Key = path, UploadId = upload.UploadId, PartETags = parts },
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await client.AbortMultipartUploadAsync(bucket, path, upload.UploadId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Left to the lifecycle rules of the bucket.
                }

                throw;
            }
        }

        public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                await client.GetObjectMetadataAsync(bucket, path, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
            => client.DeleteObjectAsync(bucket, path, cancellationToken);

        public Task<bool> MoveDirectoryAsync(string path, string newPath, CancellationToken cancellationToken)
            => throw new NotSupportedException("S3 没有目录。");

        public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
            => throw new NotSupportedException("S3 没有目录。");

        public void Dispose()
        {
        }
    }
}
