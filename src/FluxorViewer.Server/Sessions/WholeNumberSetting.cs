using System.Globalization;

namespace FluxorViewer.Server.Sessions;

/// <summary>A configuration value given on the command line as <c>--key N</c>, read as a whole number with a minimum.</summary>
internal static class WholeNumberSetting
{
    /// <summary>
    /// Reads <paramref name="key"/>: null when it isn't set, the number when it's a whole number of at least
    /// <paramref name="minimum"/>, otherwise an error naming the value, as one line for the developer.
    /// </summary>
    public static (int? Value, string? Error) Read(IConfiguration configuration, string key, int minimum) =>
        configuration[key] switch
        {
            null => (null, null),
            var value when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= minimum => (n, null),
            // Digits only, so a whole number; it failed to parse because it's too large.
            var value when value.Length > 0 && value.All(char.IsAsciiDigit) && value.TrimStart('0').Length > 0 =>
                (null, $"--{key} can be at most {int.MaxValue}, not \"{value}\"."),
            var value => (null, $"--{key} must be a whole number of at least {minimum}, not \"{value}\"."),
        };
}
