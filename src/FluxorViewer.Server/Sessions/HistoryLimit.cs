using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace FluxorViewer.Server.Sessions;

/// <summary>The most entries the Viewer keeps in a Session's History; once it's reached, the oldest are discarded.</summary>
public sealed record HistoryLimit(int Value)
{
    /// <summary>The configuration key, set on the command line as <c>--history N</c>.</summary>
    public const string Key = "history";

    /// <summary>The limit when <see cref="Key"/> isn't set: 1,000 entries per Session.</summary>
    public static readonly HistoryLimit Default = new(1000);

    /// <summary>Reads the limit set by <see cref="Key"/>, or <see cref="Default"/> when it isn't set.</summary>
    /// <param name="error">Why the value can't be used, as one line for the developer; null when it can.</param>
    public static bool TryFrom(
        IConfiguration configuration, [NotNullWhen(true)] out HistoryLimit? limit, [NotNullWhen(false)] out string? error)
    {
        var value = configuration[Key];
        (limit, error) = value switch
        {
            null => (Default, null),
            _ when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 => (new HistoryLimit(n), null),
            // Digits only, so a whole number; it failed to parse because it's too large.
            _ when value.Length > 0 && value.All(char.IsAsciiDigit) && value.TrimStart('0').Length > 0 =>
                ((HistoryLimit?)null, $"--history can be at most {int.MaxValue}, not \"{value}\"."),
            _ => (null, $"--history must be a whole number of at least 1, not \"{value}\"."),
        };
        return limit is not null;
    }
}
