using System.Globalization;

namespace FluxorViewer.Middleware;

/// <summary>Sizes as a developer reads them in a Serialisation error: <c>812 bytes</c>, <c>3 KB</c>, <c>12.4 MB</c>.</summary>
internal static class ByteSize
{
    private const double Kilobyte = 1024;
    private const double Megabyte = 1024 * Kilobyte;

    /// <summary><paramref name="bytes"/> in bytes below 1 KB, else in KB or MB (of 1024) to one decimal place, dropping <c>.0</c>.</summary>
    public static string Readable(long bytes) => bytes switch
    {
        < (long)Kilobyte => $"{bytes} bytes",
        < (long)Megabyte => string.Create(CultureInfo.InvariantCulture, $"{bytes / Kilobyte:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / Megabyte:0.#} MB"),
    };
}
