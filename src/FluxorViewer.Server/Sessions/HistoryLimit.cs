using System.Globalization;

namespace FluxorViewer.Server.Sessions;

/// <summary>The most entries the Viewer keeps in a Session's History; once it's reached, the oldest are discarded.</summary>
public sealed record HistoryLimit(int Value)
{
    /// <summary>The configuration key, set on the command line as <c>--history N</c>.</summary>
    public const string Key = "history";

    public static readonly HistoryLimit Default = new(1000);

    /// <summary>The limit set by <see cref="Key"/>, or <see cref="Default"/> when it isn't set.</summary>
    /// <exception cref="InvalidOperationException">The value isn't a whole number of at least 1.</exception>
    public static HistoryLimit From(IConfiguration configuration) => configuration[Key] switch
    {
        null => Default,
        var value when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit >= 1 => new HistoryLimit(limit),
        var value => throw new InvalidOperationException($"--history must be a whole number of at least 1, not \"{value}\"."),
    };
}
