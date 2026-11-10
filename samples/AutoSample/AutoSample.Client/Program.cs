using AutoSample.Client.Store;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddSampleFluxor();

await builder.Build().RunAsync();
