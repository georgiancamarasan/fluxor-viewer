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

    public Uri BaseAddress { get; }

    public Uri HubUrl => new(BaseAddress, IngestHub.Path);

    public SessionRegistry Registry => Services.GetRequiredService<SessionRegistry>();

    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureLogging(logging => logging
            .AddProvider(new CapturingProvider(Logs))
            .AddFilter<CapturingProvider>(null, LogLevel.Debug)); // appsettings.json would otherwise hide Debug.
    }

    public async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder().WithUrl(HubUrl).Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public IEnumerable<(LogLevel Level, string Message)> IngestLogs(LogLevel level) =>
        Logs.Where(l => l.Category == typeof(IngestRejections).FullName && l.Level == level).Select(l => (l.Level, l.Message));

    private sealed class CapturingProvider(ConcurrentQueue<(LogLevel, string, string)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(logs, categoryName);

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

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10, Ct);
        }
    }
}
