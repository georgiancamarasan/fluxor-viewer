using System.Globalization;

namespace FluxorViewer.Server.CommandLine;

/// <summary>
/// A configuration value given on the command line as <c>--key N</c>: a whole number with a minimum, an optional
/// maximum and a default.
/// </summary>
/// <param name="Key">The configuration key, also the command-line option's name.</param>
/// <param name="Minimum">The smallest value allowed.</param>
/// <param name="Default">The value when the key isn't set.</param>
/// <param name="Maximum">The largest value allowed; null for any value an <see cref="int"/> holds.</param>
internal sealed record WholeNumberSetting(string Key, int Minimum, int Default, int? Maximum = null)
{
    /// <summary>
    /// Reads the value: <see cref="Default"/> when it isn't set, the number when it's a whole number from
    /// <see cref="Minimum"/> to <see cref="Maximum"/>, otherwise null and an error naming the value, as one line for the developer.
    /// </summary>
    public (int? Value, string? Error) Read(IConfiguration configuration) =>
        configuration[Key] switch
        {
            null => (Default, null),
            var value when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number >= Minimum && number <= (Maximum ?? int.MaxValue) => (number, null),
            var value => (null, ErrorFor(value)),
        };

    private string ErrorFor(string value) =>
        Maximum is { } maximum ? $"--{Key} must be a whole number from {Minimum} to {maximum}, not \"{value}\"."
        : IsTooLargeForInt(value) ? $"--{Key} can be at most {int.MaxValue}, not \"{value}\"."
        : $"--{Key} must be a whole number of at least {Minimum}, not \"{value}\".";

    // Digits only, so a whole number; it failed to parse because it's too large.
    private static bool IsTooLargeForInt(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) && value.TrimStart('0').Length > 0;
}
