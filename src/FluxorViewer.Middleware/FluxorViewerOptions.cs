using System.Reflection;

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
    /// Maximum number of Actions waiting to be sent; beyond this they're dropped and the Viewer shows a Resync.
    /// Must be at least 1.
    /// </summary>
    public int BufferSize { get; set; } = 1000;

    /// <summary>
    /// When false (the default) nothing is registered: no connection is made and nothing is serialised.
    /// Typically set to <c>IsDevelopment()</c>.
    /// </summary>
    public bool Enabled { get; set; }

    internal IReadOnlyList<Func<object, bool>> IgnoredActionPredicates => ignoredActionPredicates;

    /// <summary>Don't report Actions for which <paramref name="predicate"/> returns true.</summary>
    public FluxorViewerOptions IgnoreAction(Func<object, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ignoredActionPredicates.Add(predicate);
        return this;
    }

    internal void Validate()
    {
        var problems = new List<(string Option, string Message)>();

        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            problems.Add((nameof(ServerUrl), $"ServerUrl '{ServerUrl}' must be an absolute http or https URL."));

        if (string.IsNullOrWhiteSpace(AppName))
            problems.Add((nameof(AppName), "AppName must not be blank."));

        if (BufferSize < 1)
            problems.Add((nameof(BufferSize), $"BufferSize must be at least 1, but was {BufferSize}."));

        switch (problems)
        {
            case []:
                return;
            case [(nameof(BufferSize), var message)]:
                throw new ArgumentOutOfRangeException(nameof(BufferSize), BufferSize, $"FluxorViewer: {message}");
            case [var (option, message)]:
                throw new ArgumentException($"FluxorViewer: {message}", option);
            default:
                throw new ArgumentException(
                    "FluxorViewer options are invalid:" + string.Concat(problems.Select(p => $"{Environment.NewLine}- {p.Message}")));
        }
    }
}
