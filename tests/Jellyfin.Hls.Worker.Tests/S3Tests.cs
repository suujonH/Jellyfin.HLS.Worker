using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Jellyfin.Hls.Worker.Configuration;
using Jellyfin.Hls.Worker.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

/// <summary>
/// A test against the S3 service at HLS_WORKER_TEST_S3 (such as http://localhost:8333), with the credentials in
/// HLS_WORKER_TEST_S3_KEY and HLS_WORKER_TEST_S3_SECRET.
/// </summary>
public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (string.IsNullOrEmpty(S3Tests.Endpoint))
        {
            Skip = "没有设置 HLS_WORKER_TEST_S3。";
        }
    }
}

public sealed class S3Tests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hls-worker-s3-" + Guid.NewGuid().ToString("N"));

    public S3Tests()
    {
        Directory.CreateDirectory(_directory);
    }

    internal static string? Endpoint => Environment.GetEnvironmentVariable("HLS_WORKER_TEST_S3");

    private static string Key => Environment.GetEnvironmentVariable("HLS_WORKER_TEST_S3_KEY") ?? "any";

    private static string Secret => Environment.GetEnvironmentVariable("HLS_WORKER_TEST_S3_SECRET") ?? "any";

    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(65L << 20, 64L << 20)]
    [InlineData(10_000L * (64L << 20), 64L << 20)]
    [InlineData((10_000L * (64L << 20)) + 1, 65L << 20)]
    [InlineData(1L << 40, 105L << 20)]
    [InlineData(10_000L * (5L << 30), 5L << 30)]
    public void GetPartSize_KeepsToTenThousandPartsOfWholeMebibytes(long length, long expected)
    {
        var size = S3Connection.GetPartSize(length);

        Assert.Equal(expected, size);
        Assert.True((length + size - 1) / size <= 10_000);
    }

    [Fact]
    public void GetPartSize_RefusesAFileTooLargeForFiveGibibyteParts()
        => Assert.Throws<InvalidDataException>(() => S3Connection.GetPartSize((10_000L * (5L << 30)) + 1));

    [S3Fact]
    public async Task Put_UploadsALargeFileInParts_AndASmallOneAtOnce()
    {
        var bucket = "hls-worker-test-" + Guid.NewGuid().ToString("N")[..8];
        using var client = new AmazonS3Client(new BasicAWSCredentials(Key, Secret), new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true });
        await client.PutBucketAsync(bucket);
        using var connection = StorageConnection.Create(
            new ConnectionSettings { Name = "s3", Type = ConnectionType.S3, Endpoint = Endpoint!, Bucket = bucket, AccessKey = Key, SecretKey = Secret, PartConcurrency = 2 },
            NullLogger.Instance);
        using var session = await connection.OpenAsync(CancellationToken.None);

        // Three parts, the last one short.
        var large = Path.Combine(_directory, "large.mkv");
        var bytes = new byte[(150 << 20) + 12345];
        new Random(1).NextBytes(bytes);
        await File.WriteAllBytesAsync(large, bytes);
        var small = Path.Combine(_directory, "small.srt");
        await File.WriteAllTextAsync(small, "1\n");

        var sent = new System.Collections.Concurrent.ConcurrentQueue<long>();
        await session.PutAsync("media/large.mkv", large, CancellationToken.None, sent.Enqueue);
        Assert.Equal(bytes.Length, sent.Max());
        await session.PutAsync("media/small.srt", small, CancellationToken.None);

        Assert.True(await session.ExistsAsync("media/large.mkv", CancellationToken.None));
        using (var response = await client.GetObjectAsync(bucket, "media/large.mkv"))
        using (var copy = new MemoryStream())
        {
            await response.ResponseStream.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        Assert.Empty((await client.ListMultipartUploadsAsync(bucket)).MultipartUploads ?? []);
        await session.DeleteAsync("media/large.mkv", CancellationToken.None);
        await session.DeleteAsync("media/small.srt", CancellationToken.None);
        Assert.False(await session.ExistsAsync("media/large.mkv", CancellationToken.None));
        await client.DeleteBucketAsync(bucket);
    }
}
