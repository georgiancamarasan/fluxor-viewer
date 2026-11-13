using System.Diagnostics.CodeAnalysis;
using FluxorViewer.Server.CommandLine;

namespace FluxorViewer.Server.Sessions;

/// <summary>The most disconnected Sessions the Viewer keeps; past it, the one that disconnected longest ago is forgotten.</summary>
public sealed record DisconnectedSessionLimit(int Value)
{
    /// <summary>The configuration key, set on the command line as <c>--disconnected N</c>.</summary>
    public const string Key = "disconnected";

    /// <summary>The limit when <see cref="Key"/> isn't set: 10 disconnected Sessions.</summary>
    public static readonly DisconnectedSessionLimit Default = new(10);

    // 0 is allowed: a Session is forgotten as soon as it disconnects.
    private static readonly WholeNumberSetting Setting = new(Key, Minimum: 0, Default.Value);

    /// <summary>Reads the limit set by <see cref="Key"/>, or <see cref="Default"/> when it isn't set.</summary>
    /// <param name="error">Why the value can't be used, as one line for the developer; null when it can.</param>
    public static bool TryFrom(
        IConfiguration configuration, [NotNullWhen(true)] out DisconnectedSessionLimit? limit, [NotNullWhen(false)] out string? error)
    {
        (var value, error) = Setting.Read(configuration);
        limit = value is { } sessions ? new DisconnectedSessionLimit(sessions) : null;
        return limit is not null;
    }
}
