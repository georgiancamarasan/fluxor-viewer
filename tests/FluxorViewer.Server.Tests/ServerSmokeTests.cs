using Microsoft.AspNetCore.Mvc.Testing;

namespace FluxorViewer.Server.Tests;

// Proves the Viewer host boots under WebApplicationFactory; ingest hub tests build on this.
public class ServerSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Server_starts_and_serves_root()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
