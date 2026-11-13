namespace FluxorViewer.Protocol;

/// <summary>
/// How the Viewer words a deliberate rejection of a message, so the middleware can tell it apart from an unexpected
/// error in the Viewer, which SignalR also reports as a <c>HubException</c>.
/// </summary>
/// <remarks>
/// The client doesn't see the message as thrown: SignalR wraps it as "An unexpected error occurred invoking 'X' on
/// the server. HubException: FluxorViewer: …", and reports any other exception as that first sentence alone.
/// </remarks>
public static class Rejection
{
    /// <summary>What every rejection message starts with, before SignalR wraps it.</summary>
    public const string Prefix = "FluxorViewer: ";

    /// <summary>The message the Viewer throws to reject something for <paramref name="reason"/>.</summary>
    public static string Message(string reason) => Prefix + reason;

    /// <summary>Whether <paramref name="message"/>, as the client sees it, is a deliberate rejection by the Viewer.</summary>
    public static bool IsRejection(string? message) => message?.Contains(Prefix, StringComparison.Ordinal) == true;

    /// <summary>The Viewer's reason, without SignalR's wrapping; the whole message if it isn't a rejection.</summary>
    public static string Reason(string message)
    {
        var at = message.IndexOf(Prefix, StringComparison.Ordinal);
        return at < 0 ? message : message[(at + Prefix.Length)..];
    }
}
