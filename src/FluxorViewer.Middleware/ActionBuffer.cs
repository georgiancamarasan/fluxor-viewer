namespace FluxorViewer.Middleware;

/// <summary>
/// Every recorded Action not yet sent, oldest first, and the base: the Feature states and Sequence just after the last
/// Action that left the buffer, sent or dropped. Snapshots are taken from the base, so they never rewind (ADR 0005).
/// Thread-safe: the store adds Actions while the sender takes them on another thread.
/// </summary>
internal sealed class ActionBuffer : IDisposable
{
    /// <summary>The most Actions sent in one message.</summary>
    internal const int MaxBatchSize = 100;

    private readonly Lock gate = new();
    private readonly int capacity;
    private readonly Queue<PendingAction> pending = new();
    private readonly FeatureStates baseStates;
    private long baseSequence;
    private long droppedCount;
    private bool snapshotDue = true;
    private bool stopped;

    // Released when there may be something to send; the sender waits on it while the buffer is empty. Uncapped, since
    // an Action may release it again before the sender has waited: that only costs a spurious wake-up.
    private readonly SemaphoreSlim wake = new(0);
    private bool signalled;

    /// <param name="initial">Every Feature's state before any Action: the first base.</param>
    /// <param name="capacity">How many Actions it holds before dropping the oldest (BufferSize).</param>
    public ActionBuffer(IReadOnlyList<TrackedState> initial, int capacity)
    {
        this.capacity = capacity;
        baseStates = new FeatureStates(initial);
    }

    /// <summary>
    /// Adds an Action to send. When the buffer is full, the oldest is dropped into the base and a Snapshot becomes due,
    /// so the Viewer records a Resync instead of building on the wrong state. Ignored once stopped.
    /// </summary>
    public void Add(PendingAction action)
    {
        lock (gate)
        {
            if (stopped)
                return;
            if (pending.Count >= capacity)
                Drop(pending.Dequeue());
            pending.Enqueue(action);
            Wake();
        }
    }

    /// <summary>
    /// What to send next, decided under one lock so a drop can't slip in between: a copy of the base when a Snapshot is
    /// due, otherwise up to <see cref="MaxBatchSize"/> Actions, possibly none. Taken Actions count as delivered: the base
    /// moves past them, so a later Snapshot never rewinds below them.
    /// </summary>
    public Outgoing TakeNext()
    {
        lock (gate)
            return snapshotDue ? TakeBase() : TakeBatch();
    }

    /// <summary>The Viewer accepted <paramref name="sent"/>: the drops it reported no longer count.</summary>
    public void SnapshotSent(BaseCopy sent)
    {
        lock (gate)
            droppedCount -= sent.DroppedCount;
    }

    /// <summary>A new connection opened: it must start with a Snapshot.</summary>
    public void Reconnected()
    {
        lock (gate)
            snapshotDue = true;
    }

    /// <summary>Stops buffering for good: nothing more is added or sent.</summary>
    public void Stop()
    {
        lock (gate)
        {
            stopped = true;
            pending.Clear();
        }
    }

    /// <summary>
    /// Waits until there may be something to send; false if <paramref name="closed"/> completes first, so an idle Session
    /// reconnects without waiting for the next Action.
    /// </summary>
    public async Task<bool> WaitForWorkAsync(Task closed, CancellationToken cancellationToken)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var woken = wake.WaitAsync(waiting.Token);
        if (await Task.WhenAny(woken, closed) == woken)
        {
            await woken;
            return true;
        }

        await waiting.CancelAsync(); // Don't leave a waiter behind to swallow a later wake-up.
        return false;
    }

    public void Dispose() => wake.Dispose();

    private BaseCopy TakeBase()
    {
        snapshotDue = false;
        return new BaseCopy(baseSequence, droppedCount, baseStates.ToList());
    }

    private Batch TakeBatch()
    {
        signalled = false;
        if (pending.Count == 0)
            return Batch.Empty;
        var before = baseStates.Copy();
        var actions = new List<PendingAction>(Math.Min(pending.Count, MaxBatchSize));
        while (actions.Count < MaxBatchSize && pending.TryDequeue(out var action))
        {
            MoveBasePast(action);
            actions.Add(action);
        }

        return new Batch(actions, before);
    }

    private void Drop(PendingAction action)
    {
        MoveBasePast(action);
        droppedCount++;
        snapshotDue = true;
    }

    private void MoveBasePast(PendingAction action)
    {
        baseStates.MovePast(action);
        baseSequence = action.Sequence;
    }

    private void Wake()
    {
        if (signalled)
            return;
        signalled = true;
        wake.Release();
    }
}

/// <summary>What the sender takes from the <see cref="ActionBuffer"/> to send next.</summary>
internal abstract record Outgoing;

/// <summary>The base, to send as a Snapshot, copied under the lock to serialise outside it (the states are immutable).</summary>
/// <param name="LastSequence">The Sequence of the last Action that left the buffer, sent or dropped.</param>
/// <param name="DroppedCount">Actions dropped since the last Snapshot the Viewer accepted.</param>
internal sealed record BaseCopy(long LastSequence, long DroppedCount, IReadOnlyList<TrackedState> Features) : Outgoing;

/// <summary>Actions to send, in as few messages as fit; empty when there's nothing to send.</summary>
/// <param name="StatesBefore">
/// Every Feature's state just before the first of <paramref name="Actions"/>: the base they left. Null when empty.
/// </param>
internal sealed record Batch(IReadOnlyList<PendingAction> Actions, FeatureStates? StatesBefore) : Outgoing
{
    public static readonly Batch Empty = new([], null);

    /// <summary>
    /// A Snapshot of the state just after the Action at <paramref name="index"/>, reporting <paramref name="droppedCount"/>
    /// Actions dropped up to it.
    /// </summary>
    public BaseCopy SnapshotAfter(int index, long droppedCount)
    {
        var states = StatesBefore!.Copy();
        foreach (var action in Actions.Take(index + 1))
            states.MovePast(action);
        return new BaseCopy(Actions[index].Sequence, droppedCount, states.ToList());
    }
}
