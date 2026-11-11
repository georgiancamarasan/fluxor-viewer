using System.Collections.Concurrent;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware.Tests;

/// <summary>Stands in for the Viewer: records what each Session's connection sends.</summary>
internal sealed class FakeViewer : IViewerConnectionFactory
{
    private readonly ConcurrentQueue<FakeConnection> connections = new();
    private readonly TaskCompletionSource<FakeConnection> firstConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes connects only when released; until then StartAsync waits.</summary>
    public TaskCompletionSource ConnectGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? ConnectFailure { get; init; }

    /// <summary>When set, sending Actions throws this.</summary>
    public Exception? ActionsFailure { get; set; }

    public IReadOnlyCollection<FakeConnection> Connections => connections;

    public Task<FakeConnection> FirstConnection => firstConnection.Task;

    public FakeViewer(bool connectImmediately = true)
    {
        if (connectImmediately)
            ConnectGate.SetResult();
    }

    public IViewerConnection Create(Uri serverUrl)
    {
        var connection = new FakeConnection(this, serverUrl);
        connections.Enqueue(connection);
        firstConnection.TrySetResult(connection);
        return connection;
    }

    internal sealed class FakeConnection(FakeViewer viewer, Uri serverUrl) : IViewerConnection
    {
        private readonly TaskCompletionSource<object> firstMessage = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Uri ServerUrl { get; } = serverUrl;

        public ConcurrentQueue<object> Sent { get; } = new();

        public CancellationToken StartToken { get; private set; }

        public Task<object> FirstMessage => firstMessage.Task;

        public Task Disposed => disposed.Task;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartToken = cancellationToken;
            await viewer.ConnectGate.Task.WaitAsync(cancellationToken);
            if (viewer.ConnectFailure is { } failure)
                throw failure;
        }

        public Task SendSnapshotAsync(Snapshot snapshot, CancellationToken cancellationToken)
        {
            Sent.Enqueue(snapshot);
            firstMessage.TrySetResult(snapshot);
            return Task.CompletedTask;
        }

        public Task SendActionsAsync(IReadOnlyList<ActionRecorded> actions, CancellationToken cancellationToken)
        {
            if (viewer.ActionsFailure is { } failure)
                throw failure;
            foreach (var action in actions)
                Sent.Enqueue(action);
            return Task.CompletedTask;
        }

        public IReadOnlyList<ActionRecorded> Actions => Sent.OfType<ActionRecorded>().ToList();

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
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerFactory factory) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            factory.Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }
}
