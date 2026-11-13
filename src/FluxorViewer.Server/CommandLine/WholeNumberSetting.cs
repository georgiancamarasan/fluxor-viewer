using System.Globalization;

namespace FluxorViewer.Server.CommandLine;

/// <summary>A configuration value given on the command line as <c>--key N</c>: a whole number with a minimum and a default.</summary>
/// <param name="Key">The configuration key, also the command-line option's name.</param>
/// <param name="Minimum">The smallest value allowed.</param>
/// <param name="Default">The value when the key isn't set.</param>
internal sealed record WholeNumberSetting(string Key, int Minimum, int Default)
{
    /// <summary>
    /// Reads the value: <see cref="Default"/> when it isn't set, the number when it's a whole number of at least
    /// <see cref="Minimum"/>, otherwise null and an error naming the value, as one line for the developer.
    /// </summary>
    public (int? Value, string? Error) Read(IConfiguration configuration) =>
        configuration[Key] switch
        {
            null => (Default, null),
            var value when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= Minimum => (number, null),
            // Digits only, so a whole number; it failed to parse because it's too large.
            var value when value.Length > 0 && value.All(char.IsAsciiDigit) && value.TrimStart('0').Length > 0 =>
                (null, $"--{Key} can be at most {int.MaxValue}, not \"{value}\"."),
            var value => (null, $"--{Key} must be a whole number of at least {Minimum}, not \"{value}\"."),
        };
}
