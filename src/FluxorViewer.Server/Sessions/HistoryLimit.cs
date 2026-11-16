using System.Diagnostics.CodeAnalysis;
using FluxorViewer.Server.CommandLine;

namespace FluxorViewer.Server.Sessions;

/// <summary>The most entries the Viewer keeps in a Session's History; once it's reached, the oldest are discarded.</summary>
public sealed record HistoryLimit(int Value)
{
    /// <summary>The configuration key, set on the command line as <c>--history N</c>.</summary>
    public const string Key = "history";

    /// <summary>The limit when <see cref="Key"/> isn't set: 1,000 entries per Session.</summary>
    public static readonly HistoryLimit Default = new(1000);

    private static readonly WholeNumberSetting Setting = new(Key, new WholeNumberRange(Minimum: 1), Default.Value);

    /// <summary>Reads the limit set by <see cref="Key"/>, or <see cref="Default"/> when it isn't set.</summary>
    /// <param name="error">Why the value can't be used, as one line for the developer; null when it can.</param>
    public static bool TryFrom(
        IConfiguration configuration, [NotNullWhen(true)] out HistoryLimit? limit, [NotNullWhen(false)] out string? error)
    {
        (var value, error) = Setting.Read(configuration);
        limit = value is { } entries ? new HistoryLimit(entries) : null;
        return limit is not null;
    }
}
