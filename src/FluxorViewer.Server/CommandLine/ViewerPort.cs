using System.Diagnostics.CodeAnalysis;

namespace FluxorViewer.Server.CommandLine;

/// <summary>The port the Viewer listens on, on localhost only; Host apps point their <c>ServerUrl</c> at it.</summary>
public sealed record ViewerPort(int Value)
{
    /// <summary>The configuration key, set on the command line as <c>--port N</c>.</summary>
    public const string Key = "port";

    /// <summary>The port when <see cref="Key"/> isn't set: 5299, the middleware's default <c>ServerUrl</c>.</summary>
    public static readonly ViewerPort Default = new(5299);

    // 0 (any free port) isn't allowed: the Host app's ServerUrl needs a port that stays the same between runs.
    private static readonly WholeNumberSetting Setting = new(Key, Minimum: 1, Default.Value, Maximum: 65535);

    /// <summary>Where the Viewer listens, as the developer opens it and a Host app's <c>ServerUrl</c> gives it.</summary>
    public string Url => $"http://localhost:{Value}";

    /// <summary>Reads the port set by <see cref="Key"/>, or <see cref="Default"/> when it isn't set.</summary>
    /// <param name="error">Why the value can't be used, as one line for the developer; null when it can.</param>
    public static bool TryFrom(
        IConfiguration configuration, [NotNullWhen(true)] out ViewerPort? port, [NotNullWhen(false)] out string? error)
    {
        (var value, error) = Setting.Read(configuration);
        port = value is { } number ? new ViewerPort(number) : null;
        return port is not null;
    }
}
