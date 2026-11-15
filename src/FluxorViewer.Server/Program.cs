using FluxorViewer.Protocol;
using FluxorViewer.Server.Components;
using FluxorViewer.Server.Ingest;
using FluxorViewer.Server.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
if (!HistoryLimit.TryFrom(builder.Configuration, out var historyLimit, out var historyError))
{
    Console.Error.WriteLine(historyError);
    return 1;
}
if (!DisconnectedSessionLimit.TryFrom(builder.Configuration, out var disconnectedLimit, out var disconnectedError))
{
    Console.Error.WriteLine(disconnectedError);
    return 1;
}
builder.Services.AddSingleton(new SessionLimits(historyLimit, disconnectedLimit));
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionRegistry>());
builder.Services.AddSingleton<IngestRejections>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Real Snapshots easily exceed SignalR's 32 KB default; the Viewer only listens on localhost.
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = JsonLimits.MaxMessageSize);

// WebAssembly Host apps connect from the browser, so allow any loopback origin.
builder.Services.AddCors(o => o.AddPolicy(IngestRules.CorsPolicy, p => p
    .SetIsOriginAllowed(IngestRules.IsAllowedOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors();
// Buffer a whole message before pausing the connection. At the 64 KB default, the hub reads a large message 64 KB at a
// time and re-scans everything buffered for its end after each read, so a 32 MB message took seconds, not milliseconds.
app.MapHub<IngestHub>(IngestHub.Path, o => o.ApplicationMaxBufferSize = JsonLimits.MaxMessageSize)
    .RequireCors(IngestRules.CorsPolicy);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

return 0;
