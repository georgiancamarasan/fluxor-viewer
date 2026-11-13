using System.Diagnostics.CodeAnalysis;

namespace FluxorViewer.Server.Sessions;

/// <summary>The most disconnected Sessions the Viewer keeps; past it, the one that disconnected longest ago is forgotten.</summary>
public sealed record DisconnectedSessionLimit(int Value)
{
    /// <summary>The configuration key, set on the command line as <c>--disconnected N</c>.</summary>
    public const string Key = "disconnected";

    /// <summary>The limit when <see cref="Key"/> isn't set: 10 disconnected Sessions.</summary>
    public static readonly DisconnectedSessionLimit Default = new(10);

    /// <summary>Reads the limit set by <see cref="Key"/>, or <see cref="Default"/> when it isn't set. 0 forgets a Session as soon as it disconnects.</summary>
    /// <param name="error">Why the value can't be used, as one line for the developer; null when it can.</param>
    public static bool TryFrom(
        IConfiguration configuration, [NotNullWhen(true)] out DisconnectedSessionLimit? limit, [NotNullWhen(false)] out string? error)
    {
        (var value, error) = WholeNumberSetting.Read(configuration, Key, minimum: 0);
        limit = error is null ? value is { } n ? new DisconnectedSessionLimit(n) : Default : null;
        return limit is not null;
    }
}
