using System.Collections.Concurrent;
using FluxorViewer.Protocol;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.SignalR;

namespace FluxorViewer.Server.Ingest;

/// <summary>
/// Receives Sessions from the middleware. Owns the protocol rules: version check on Snapshots, one Session per
/// connection, and message shape. Everything accepted goes to the <see cref="ISessionRegistry"/>.
/// </summary>
public sealed class IngestHub(ISessionRegistry registry, IngestRejections rejections) : Hub
{
    public const string Path = "/hub/ingest";

    private const string BoundSessionKey = "FluxorViewer.SessionId";

    public void Snapshot(Snapshot snapshot)
    {
        if (snapshot is null)
            throw Reject("a Snapshot is required.");

        if (!snapshot.ProtocolVersion.IsCompatibleWith(ProtocolVersion.Current))
        {
            var message = $"protocol version {snapshot.ProtocolVersion} is not compatible with this Viewer's " +
                          $"{ProtocolVersion.Current}. Use the same major version of FluxorViewer.Middleware and the Viewer.";
            rejections.VersionMismatch(snapshot.SessionId, snapshot.AppName, message);
            throw new HubException($"FluxorViewer: {message}");
        }

        if (snapshot.SessionId == Guid.Empty)
            throw Reject("the Snapshot has an empty SessionId.");
        if (snapshot.Features is null)
            throw Reject("the Snapshot has no Features list.");
        if (snapshot.Features.GroupBy(f => f?.Name).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw Reject($"the Snapshot lists Feature '{duplicate.Key}' more than once.");
        if (BoundSession is { } bound && bound != snapshot.SessionId)
            throw Reject($"this connection already reports Session {bound}; open a new connection for Session {snapshot.SessionId}.");

        BoundSession = snapshot.SessionId;
        registry.SnapshotReceived(Context.ConnectionId, snapshot);
    }

    public void Actions(ActionRecorded[] actions)
    {
        if (BoundSession is not { } bound)
            throw Reject("Actions arrived before a Snapshot on this connection; send a Snapshot first.");
        if (actions is null || actions.Any(a => a is null))
            throw Reject("the Actions batch is missing or contains null entries.");
        if (actions.FirstOrDefault(a => a.SessionId != bound) is { } stray)
            throw Reject($"this connection reports Session {bound}, but an Action is for Session {stray.SessionId}.");

        registry.ActionsReceived(Context.ConnectionId, actions);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.ConnectionClosed(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    private Guid? BoundSession
    {
        get => Context.Items.TryGetValue(BoundSessionKey, out var id) ? (Guid?)id : null;
        set => Context.Items[BoundSessionKey] = value;
    }

    private HubException Reject(string reason)
    {
        rejections.Rejected(Context, reason);
        return new HubException($"FluxorViewer: {reason}");
    }
}

/// <summary>
/// Logs rejections without flooding: version mismatches once per Session, anything else at Warning the first time
/// per connection and Debug after that.
/// </summary>
public sealed class IngestRejections(ILogger<IngestRejections> logger)
{
    private const string WarnedKey = "FluxorViewer.Warned";

    private readonly ConcurrentDictionary<Guid, byte> versionMismatches = new();

    public void VersionMismatch(Guid sessionId, string? appName, string message)
    {
        if (versionMismatches.TryAdd(sessionId, 0))
            logger.LogWarning("Rejected Session {SessionId} ({AppName}): {Reason}", sessionId, appName, message);
    }

    public void Rejected(HubCallerContext context, string reason)
    {
        var level = context.Items.TryAdd(WarnedKey, true) ? LogLevel.Warning : LogLevel.Debug;
        logger.Log(level, "Rejected a message on connection {ConnectionId}: {Reason}", context.ConnectionId, reason);
    }
}
