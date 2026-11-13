using System.Collections.Concurrent;
using FluxorViewer.Server.Ingest;
using FluxorViewer.Server.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FluxorViewer.Server.Tests;

/// <summary>The real Viewer on real Kestrel (a free port), so tests use the same WebSocket path as the middleware.</summary>
public sealed class Viewer : WebApplicationFactory<Program>
{
    /// <summary>A Viewer on a free port.</summary>
    public Viewer() : this(0)
    {
    }

    /// <summary>A Viewer on <paramref name="port"/>, e.g. the port of one that was stopped, to restart it.</summary>
    internal Viewer(int port) : this(port, new Dictionary<string, string?>())
    {
    }

    /// <summary>A Viewer started with these configuration <paramref name="settings"/>, as if given on the command line.</summary>
    internal Viewer(IReadOnlyDictionary<string, string?> settings) : this(0, settings)
    {
    }

    private Viewer(int port, IReadOnlyDictionary<string, string?> settings)
    {
        this.settings = settings;
        UseKestrel(port);
        StartServer();
        BaseAddress = new Uri(Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
    }

    private readonly IReadOnlyDictionary<string, string?> settings;

    /// <summary>Where the Viewer listens, as a Host app's <c>ServerUrl</c> would give it.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The ingest hub's address on this Viewer.</summary>
    public Uri HubUrl => new(BaseAddress, IngestHub.Path);

    /// <summary>The Viewer's Session registry, as the UI injects it.</summary>
    public SessionRegistry Registry => Services.GetRequiredService<SessionRegistry>();

    /// <summary>Everything the Viewer logged, Debug and up, in order.</summary>
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureLogging(logging => logging
            .AddProvider(new CapturingProvider(Logs))
            .AddFilter<CapturingProvider>(null, LogLevel.Debug)); // appsettings.json would otherwise hide Debug.
    }

    /// <summary>Opens a SignalR connection to the ingest hub, as the middleware would.</summary>
    public async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder().WithUrl(HubUrl).Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    /// <summary>What the ingest hub logged about rejections, at <paramref name="level"/>.</summary>
    public IEnumerable<(LogLevel Level, string Message)> IngestLogs(LogLevel level) =>
        Logs.Where(l => l.Category == typeof(IngestRejections).FullName && l.Level == level).Select(l => (l.Level, l.Message));

    private sealed class CapturingProvider(ConcurrentQueue<(LogLevel, string, string)> logs) : ILoggerProvider
    {
        /// <inheritdoc />
        public ILogger CreateLogger(string categoryName) => new Logger(logs, categoryName);

        /// <inheritdoc />
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<(LogLevel, string, string)> logs, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                logs.Enqueue((logLevel, category, formatter(state, exception)));
        }
    }
}
