using System.Reflection;
using FluxorViewer.Protocol;

namespace FluxorViewer.Middleware;

/// <summary>Configures how a Host app reports its Sessions to the Viewer.</summary>
public sealed class FluxorViewerOptions
{
    private readonly List<Func<object, bool>> ignoredActionPredicates = [];

    /// <summary>Base URL of the Viewer. Must be an absolute http or https URL.</summary>
    public string ServerUrl { get; set; } = "http://localhost:5299";

    /// <summary>Name the Viewer shows for this Host app. Defaults to the entry assembly name.</summary>
    public string AppName { get; set; } = Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown";

    /// <summary>
    /// Maximum number of Actions waiting to be sent, while connected or not (e.g. before the Viewer is started);
    /// beyond this the oldest are dropped and the Viewer shows a Resync. Must be at least 1.
    /// </summary>
    public int BufferSize { get; set; } = 1000;

    /// <summary>
    /// How deeply a Payload or Feature state may nest; anything deeper is sent as a Serialisation error. Must be from
    /// 1 to 1024, the most the Viewer reads.
    /// </summary>
    public int MaxDepth { get; set; } = 64;

    /// <summary>
    /// The largest a Payload or Feature state's JSON may be, in UTF-8 bytes; anything larger is sent as a Serialisation
    /// error naming its size. Must be from 1 to 32 MB, the largest message the Viewer accepts.
    /// </summary>
    public int MaxValueSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// When false (the default) nothing is registered: no connection is made and nothing is serialised.
    /// Typically set to <c>IsDevelopment()</c>.
    /// </summary>
    public bool Enabled { get; set; }

    internal IReadOnlyList<Func<object, bool>> IgnoredActionPredicates => ignoredActionPredicates;

    /// <summary>
    /// The largest message, in bytes, the Viewer accepts: always <see cref="JsonLimits.MaxMessageSize"/>, but smaller in
    /// tests so they don't need tens of MB to reach it.
    /// </summary>
    internal int MaxMessageSize { get; set; } = JsonLimits.MaxMessageSize;

    /// <summary>
    /// Don't report Actions for which <paramref name="predicate"/> returns true. They take no Sequence, and the state they
    /// change is reported with the next reported Action. A predicate that throws counts as false and is logged.
    /// </summary>
    public FluxorViewerOptions IgnoreAction(Func<object, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ignoredActionPredicates.Add(predicate);
        return this;
    }

    /// <summary>Checks every option; throws for the first problem alone, or for all of them on one line.</summary>
    /// <exception cref="ArgumentException">An option is invalid (<see cref="ArgumentOutOfRangeException"/> for a number).</exception>
    internal void Validate()
    {
        switch (Problems().ToList())
        {
            case []:
                return;
            case [var only]:
                throw only.Exception;
            case var problems:
                throw new ArgumentException($"FluxorViewer: options are invalid. {string.Join(' ', problems.Select(p => p.Message))}");
        }
    }

    private IEnumerable<Problem> Problems()
    {
        if (!IsHttpUrl(ServerUrl))
            yield return Problem.Invalid(nameof(ServerUrl), $"ServerUrl '{ServerUrl}' must be an absolute http or https URL.");
        if (string.IsNullOrWhiteSpace(AppName))
            yield return Problem.Invalid(nameof(AppName), "AppName must not be blank.");
        if (BufferSize < 1)
            yield return Problem.OutOfRange(nameof(BufferSize), BufferSize, $"BufferSize must be at least 1, but was {BufferSize}.");
        if (MaxDepth is < 1 or > JsonLimits.MaxDepth)
            yield return Problem.OutOfRange(nameof(MaxDepth), MaxDepth, $"MaxDepth must be between 1 and {JsonLimits.MaxDepth}, but was {MaxDepth}.");
        if (MaxValueSize < 1 || MaxValueSize > MaxMessageSize)
            yield return Problem.OutOfRange(nameof(MaxValueSize), MaxValueSize,
                $"MaxValueSize must be between 1 and {MaxMessageSize} bytes ({ByteSize.Readable(MaxMessageSize)}), but was {MaxValueSize}.");
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>One invalid option: its message, and what to throw when it's the only problem.</summary>
    private sealed record Problem(string Message, ArgumentException Exception)
    {
        public static Problem Invalid(string option, string message) =>
            new(message, new ArgumentException($"FluxorViewer: {message}", option));

        public static Problem OutOfRange(string option, int value, string message) =>
            new(message, new ArgumentOutOfRangeException(option, value, $"FluxorViewer: {message}"));
    }
}
