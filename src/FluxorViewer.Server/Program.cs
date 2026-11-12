using FluxorViewer.Server.Components;
using FluxorViewer.Server.Ingest;
using FluxorViewer.Server.Sessions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<ISessionRegistry>(sp => sp.GetRequiredService<SessionRegistry>());
builder.Services.AddSingleton<IngestRejections>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Real Snapshots easily exceed SignalR's 32 KB default; the Viewer only listens on localhost.
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = IngestLimits.MaximumMessageSize);

// WebAssembly Host apps connect from the browser, so allow any loopback origin.
builder.Services.AddCors(o => o.AddPolicy(IngestLimits.CorsPolicy, p => p
    .SetIsOriginAllowed(IngestLimits.IsAllowedOrigin)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.MapHub<IngestHub>(IngestHub.Path).RequireCors(IngestLimits.CorsPolicy);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
