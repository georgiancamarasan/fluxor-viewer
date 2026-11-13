using FluxorViewer.Server.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

using static FluxorViewer.Server.Tests.Messages;

namespace FluxorViewer.Server.Tests;

/// <summary>
/// Sessions reported straight to a registry, each on a connection with the name the test gives it, so tests can say
/// which Sessions are kept by name.
/// </summary>
internal sealed class NamedSessions(SessionRegistry registry)
{
    private readonly Dictionary<string, Guid> ids = [];

    /// <summary>A registry that keeps at most <paramref name="disconnected"/> disconnected Sessions, other limits at their defaults.</summary>
    public static SessionRegistry Registry(int disconnected) => new(
        NullLogger<SessionRegistry>.Instance, TimeProvider.System, SessionLimits.Default with { Disconnected = new DisconnectedSessionLimit(disconnected) });

    /// <summary>The SessionId of the Session started as <paramref name="name"/>.</summary>
    public Guid this[string name] => ids[name];

    /// <summary>Starts a Session for each name, in order, each with its first Snapshot on a connection of that name.</summary>
    public void Start(params string[] names)
    {
        foreach (var name in names)
        {
            ids[name] = Guid.NewGuid();
            registry.SnapshotReceived(name, NewSnapshot(ids[name]));
        }
    }

    /// <summary>Closes the connection of each name, in order.</summary>
    public void Disconnect(params string[] names)
    {
        foreach (var name in names)
            registry.ConnectionClosed(name);
    }

    /// <summary>The names of the Sessions the registry still has, in the order it first saw them.</summary>
    public string[] Kept() =>
        [.. registry.GetSessions().Select(s => ids.Single(id => id.Value == s.SessionId).Key)];
}
