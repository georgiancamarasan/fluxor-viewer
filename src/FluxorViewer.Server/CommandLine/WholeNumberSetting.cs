using System.Globalization;

namespace FluxorViewer.Server.CommandLine;

/// <summary>The whole numbers a <see cref="WholeNumberSetting"/> allows.</summary>
/// <param name="Minimum">The smallest value allowed.</param>
/// <param name="Maximum">The largest value allowed; null for any value an <see cref="int"/> holds.</param>
internal sealed record WholeNumberRange(int Minimum, int? Maximum = null)
{
    /// <summary>Whether <paramref name="number"/> is from <see cref="Minimum"/> to <see cref="Maximum"/>.</summary>
    public bool Contains(int number) => number >= Minimum && number <= (Maximum ?? int.MaxValue);
}

/// <summary>
/// A configuration value given on the command line as <c>--key N</c>: a whole number within a range, with a default.
/// </summary>
/// <param name="Key">The configuration key, also the command-line option's name.</param>
/// <param name="Allowed">The values allowed.</param>
/// <param name="Default">The value when the key isn't set.</param>
internal sealed record WholeNumberSetting(string Key, WholeNumberRange Allowed, int Default)
{
    /// <summary>
    /// Reads the value: <see cref="Default"/> when it isn't set, the number when it's a whole number within
    /// <see cref="Allowed"/>, otherwise null and an error naming the value, as one line for the developer.
    /// </summary>
    public (int? Value, string? Error) Read(IConfiguration configuration) =>
        configuration[Key] switch
        {
            null => (Default, null),
            var value when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && Allowed.Contains(number) => (number, null),
            var value => (null, ErrorFor(value)),
        };

    private string ErrorFor(string value) =>
        Allowed.Maximum is { } maximum ? $"--{Key} must be a whole number from {Allowed.Minimum} to {maximum}, not \"{value}\"."
        : IsTooLargeForInt(value) ? $"--{Key} can be at most {int.MaxValue}, not \"{value}\"."
        : $"--{Key} must be a whole number of at least {Allowed.Minimum}, not \"{value}\".";

    // Digits only, so a whole number; it failed to parse because it's too large.
    private static bool IsTooLargeForInt(string value) =>
        value.Length > 0 && value.All(char.IsAsciiDigit) && value.TrimStart('0').Length > 0;
}
