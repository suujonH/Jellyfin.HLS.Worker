using Jellyfin.Hls.Worker.Jobs;
using Xunit;

namespace Jellyfin.Hls.Worker.Tests;

public sealed class UploadLimiterTests
{
    [Fact]
    public async Task Wait_BeyondTheLimit_WaitsForARelease()
    {
        var limiter = new UploadLimiter(2);
        await limiter.WaitAsync(CancellationToken.None);
        await limiter.WaitAsync(CancellationToken.None);

        var third = limiter.WaitAsync(CancellationToken.None);
        Assert.False(third.IsCompleted);

        limiter.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, limiter.Running);
    }

    [Fact]
    public async Task Limit_Raised_StartsThoseWaiting_InOrder()
    {
        var limiter = new UploadLimiter(1);
        await limiter.WaitAsync(CancellationToken.None);
        var a = limiter.WaitAsync(CancellationToken.None);
        var b = limiter.WaitAsync(CancellationToken.None);

        limiter.Limit = 2;
        await a.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(b.IsCompleted);

        limiter.Limit = 3;
        await b.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, limiter.Running);
    }

    [Fact]
    public async Task Limit_Lowered_LetsTheRunningEnd()
    {
        var limiter = new UploadLimiter(3);
        for (var i = 0; i < 3; i++)
        {
            await limiter.WaitAsync(CancellationToken.None);
        }

        limiter.Limit = 1;
        var c = limiter.WaitAsync(CancellationToken.None);
        limiter.Release();
        limiter.Release();
        Assert.False(c.IsCompleted);
        Assert.Equal(1, limiter.Running);

        limiter.Release();
        await c.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, limiter.Running);
    }

    [Fact]
    public async Task Wait_Cancelled_GivesUpItsTurn()
    {
        var limiter = new UploadLimiter(1);
        await limiter.WaitAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var d = limiter.WaitAsync(cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d);

        limiter.Release();
        Assert.Equal(0, limiter.Running);
        Assert.True(limiter.WaitAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public void Release_WithoutAPlace_Throws()
        => Assert.Throws<InvalidOperationException>(new UploadLimiter(1).Release);
}
