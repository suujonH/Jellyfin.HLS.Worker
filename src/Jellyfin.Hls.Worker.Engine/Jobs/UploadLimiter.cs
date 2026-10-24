namespace Jellyfin.Hls.Worker.Jobs;

/// <summary>
/// Lets up to a number of uploads run at the same time, a number that can be changed while they run: a smaller one lets the
/// running uploads end, a larger one starts those waiting at once. Those waiting start in the order they came.
/// </summary>
public sealed class UploadLimiter
{
    private readonly Lock _gate = new();
    private readonly LinkedList<TaskCompletionSource> _waiting = new();
    private int _limit;
    private int _running;

    /// <summary>
    /// Initializes a new instance of the <see cref="UploadLimiter"/> class.
    /// </summary>
    /// <param name="limit">How many at the same time, at least 1.</param>
    public UploadLimiter(int limit) => _limit = Check(limit);

    /// <summary>Gets or sets how many run at the same time, at least 1.</summary>
    public int Limit
    {
        get
        {
            lock (_gate)
            {
                return _limit;
            }
        }

        set
        {
            lock (_gate)
            {
                _limit = Check(value);
                StartWaiting();
            }
        }
    }

    /// <summary>Gets how many run now.</summary>
    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// Waits for a place; each place taken is given back with <see cref="Release"/>. One that was cancelled took none.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<TaskCompletionSource> node;
        lock (_gate)
        {
            // None goes before those waiting.
            if (_waiting.Count == 0 && _running < _limit)
            {
                _running++;
                return Task.CompletedTask;
            }

            node = _waiting.AddLast(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return WaitForAsync(node, cancellationToken);
    }

    /// <summary>Gives back a place.</summary>
    /// <exception cref="InvalidOperationException">No place is taken.</exception>
    public void Release()
    {
        lock (_gate)
        {
            if (_running == 0)
            {
                throw new InvalidOperationException("没有占用的名额。");
            }

            _running--;
            StartWaiting();
        }
    }

    private static int Check(int limit) => limit >= 1 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    private async Task WaitForAsync(LinkedListNode<TaskCompletionSource> node, CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                // A place given already is the waiter's; it gives it back.
                if (node.List is not null)
                {
                    _waiting.Remove(node);
                    node.Value.TrySetCanceled(cancellationToken);
                }
            }
        }))
        {
            await node.Value.Task.ConfigureAwait(false);
        }
    }

    // Called with the gate held.
    private void StartWaiting()
    {
        while (_running < _limit && _waiting.First is { } first)
        {
            _waiting.RemoveFirst();
            _running++;
            first.Value.TrySetResult();
        }
    }
}
