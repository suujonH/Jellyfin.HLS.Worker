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
            string digest;
            var stream = File.OpenRead(localFile);
            await using (stream.ConfigureAwait(false))
            {
                // Content-MD5 is what S3 defines for the integrity of a body; it is no protection against an attacker.
#pragma warning disable CA5351
                digest = Convert.ToBase64String(await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
#pragma warning restore CA5351
            }

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

        public void Dispose()
        {
        }
    }
}
