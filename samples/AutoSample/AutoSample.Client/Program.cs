using AutoSample.Client.Store;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddSampleFluxor(viewerEnabled: builder.HostEnvironment.IsDevelopment());

await builder.Build().RunAsync();
