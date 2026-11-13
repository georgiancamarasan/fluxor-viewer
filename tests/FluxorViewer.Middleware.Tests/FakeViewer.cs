using System.Collections.Concurrent;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

/// <summary>Stands in for the Viewer: records what each Session's connection sends.</summary>
internal sealed class FakeViewer : IViewerConnectionFactory
{
    private readonly ConcurrentQueue<FakeConnection> connections = new();
    private readonly TaskCompletionSource<FakeConnection> firstConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes connects only when released; until then StartAsync waits. Released from the start unless <see cref="NotUpYet"/>.</summary>
    public TaskCompletionSource ConnectGate { get; private init; } = Released();

    /// <summary>When set, connecting throws this: the Viewer is down.</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>When set, sending a Snapshot throws this.</summary>
    public Exception? SnapshotFailure { get; set; }

    /// <summary>When set, sending Actions throws this.</summary>
    public Exception? ActionsFailure { get; set; }

    /// <summary>Sending Actions waits for this; replace it with an incomplete source to stall the sender.</summary>
    public TaskCompletionSource ActionsGate { get; set; } = Released();

    private static TaskCompletionSource Released()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    /// <summary>Every connection the middleware opened, oldest first: one per attempt.</summary>
    public IReadOnlyCollection<FakeConnection> Connections => connections;

    /// <summary>Completes with the first connection the middleware opens.</summary>
    public Task<FakeConnection> FirstConnection => firstConnection.Task;

    /// <summary>Waits for the first connection's first message, which must be a Snapshot.</summary>
    public async Task<Snapshot> FirstSnapshot()
    {
        var connection = await FirstConnection.WaitAsync(Wait.Timeout, Wait.CancellationToken);
        return Assert.IsType<Snapshot>(await connection.FirstMessage.WaitAsync(Wait.Timeout, Wait.CancellationToken));
    }

    /// <summary>A Viewer that isn't up yet: connects wait until a test releases <see cref="ConnectGate"/>.</summary>
    public static FakeViewer NotUpYet() => new() { ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };

    /// <inheritdoc />
    public IViewerConnection Create(Uri serverUrl)
    {
        var connection = new FakeConnection(this, serverUrl);
        connections.Enqueue(connection);
        firstConnection.TrySetResult(connection);
        return connection;
    }

    /// <summary>One connection attempt: records what was sent on it and fails as the <see cref="FakeViewer"/> is set to.</summary>
    internal sealed class FakeConnection(FakeViewer viewer, Uri serverUrl) : IViewerConnection
    {
        private readonly TaskCompletionSource<object> firstMessage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public Task Closed => closed.Task;

        /// <summary>The connection closes from the Viewer's side, as when the Viewer stops.</summary>
        public void Close() => closed.TrySetResult();

        /// <summary>The Viewer address the middleware connected to.</summary>
        public Uri ServerUrl { get; } = serverUrl;

        /// <summary>Every Snapshot and Action sent, in order.</summary>
        public ConcurrentQueue<object> Sent { get; } = new();

        /// <summary>The token the connect was started with, to check the middleware cancels it.</summary>
        public CancellationToken StartToken { get; private set; }

        /// <summary>Completes with the first message sent.</summary>
        public Task<object> FirstMessage => firstMessage.Task;

        /// <summary>Completes when the middleware disposes the connection, which ends that attempt.</summary>
        public Task Disposed => disposed.Task;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartToken = cancellationToken;
            await viewer.ConnectGate.Task.WaitAsync(cancellationToken);
            if (viewer.ConnectFailure is { } failure)
                throw failure;
        }

        /// <inheritdoc />
        public Task SendSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken)
        {
            if (viewer.SnapshotFailure is { } failure)
                return Task.FromException(failure);
            Sent.Enqueue(snapshot);
            firstMessage.TrySetResult(snapshot);
            return Task.CompletedTask;
        }

        private int actionSendsStarted;

        /// <summary>How many Actions sends have begun (including any waiting on <see cref="ActionsGate"/>).</summary>
        public int ActionSendsStarted => Volatile.Read(ref actionSendsStarted);

        /// <inheritdoc />
        public async Task SendActionsAsync(IReadOnlyList<ActionRecorded> actions, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref actionSendsStarted);
            await viewer.ActionsGate.Task.WaitAsync(cancellationToken);
            if (viewer.ActionsFailure is { } failure)
                throw failure;
            Batches.Enqueue(actions);
            foreach (var action in actions)
                Sent.Enqueue(action);
        }

        /// <summary>Each Actions batch sent, in order, as the middleware split them.</summary>
        public ConcurrentQueue<IReadOnlyList<ActionRecorded>> Batches { get; } = new();

        /// <summary>Every Action sent, in order.</summary>
        public IReadOnlyList<ActionRecorded> Actions => Sent.OfType<ActionRecorded>().ToList();

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Captures log entries so tests can assert on them.</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    /// <summary>Every entry logged, in order, at any level.</summary>
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new Logger(this);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider) { }

    /// <inheritdoc />
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerFactory factory) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            factory.Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
