using System.Runtime.CompilerServices;
using Fluxor;
using FluxorViewer.Protocol;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Middleware;

/// <summary>
/// Reports one store to the Viewer as a Session: tracks each Action's Changed Features, buffers it, and sends in the
/// background. Never throws into the Host app.
/// </summary>
internal sealed class SessionReporter(ReporterContext context) : IAsyncDisposable
{
    private readonly CancellationTokenSource stopping = new();
    private ChangeTracker? tracker;
    private ActionBuffer? buffer;
    private Task sending = Task.CompletedTask;
    private int disposed;

    /// <summary>Starts the Session from the store's current state. Returns without waiting for the connection.</summary>
    public void Start(IStore store)
    {
        tracker = new ChangeTracker(store.Features.Values);
        buffer = new ActionBuffer(tracker.States, context.Options.BufferSize);
        sending = new Sender(context, NewSession(), buffer).RunAsync(stopping.Token);
    }

    /// <summary>
    /// Records an Action after its reducers ran and buffers it to send. Doesn't serialise or wait. A failure is logged at
    /// Debug and the Viewer sees a gap in Sequences.
    /// </summary>
    public void Record(object action)
    {
        if (tracker is null || buffer is null || IsIgnored(action))
            return;
        try
        {
            buffer.Add(tracker.Track(action, context.Time.GetUtcNow()));
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(ex, "FluxorViewer: could not record {ActionType}.", action.GetType());
        }
    }

    // Shared by every Session: a Blazor Server app has many, all with the same predicates.
    private static readonly ConditionalWeakTable<Func<object, bool>, object> WarnedPredicates = new();

    /// <summary>
    /// Whether any IgnoreAction predicate matches <paramref name="action"/>. A predicate that throws counts as false,
    /// logged at Warning the first time it throws in the process and at Debug after that.
    /// </summary>
    private bool IsIgnored(object action)
    {
        foreach (var ignore in context.Options.IgnoredActionPredicates)
        {
            try
            {
                if (ignore(action))
                    return true;
            }
            catch (Exception ex)
            {
                var level = WarnedPredicates.TryAdd(ignore, new object()) ? LogLevel.Warning : LogLevel.Debug;
                context.Logger.Log(level, ex,
                    "FluxorViewer: an IgnoreAction predicate threw for {ActionType}; the Action is reported unless another predicate ignores it.",
                    TypeNames.Readable(action.GetType()));
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
            return;

        buffer?.Stop();
        await stopping.CancelAsync();
        try
        {
            await sending;
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(ex, "FluxorViewer: Session ended with an error.");
        }

        stopping.Dispose();
        buffer?.Dispose();
    }

    private SessionInfo NewSession() => new(
        Guid.NewGuid(),
        context.Options.AppName,
        OperatingSystem.IsBrowser() ? Runtime.WebAssembly : Runtime.Server,
        context.Time.GetUtcNow());
}

/// <summary>What a Session's reporting needs from the Host app: its options, how to connect, where to log, and the time.</summary>
internal sealed record ReporterContext(
    FluxorViewerOptions Options, IViewerConnectionFactory Connections, ILogger Logger, TimeProvider Time);
