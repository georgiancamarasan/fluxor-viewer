namespace FluxorViewer.Server.Tests;

// PK-1: proves the Viewer host boots on real Kestrel; ingest hub tests build on this.
public class ServerSmokeTests(Viewer viewer) : IClassFixture<Viewer>
{
    [Fact]
    public async Task The_Viewer_starts_and_serves_its_page()
    {
        using var client = new HttpClient { BaseAddress = viewer.BaseAddress };

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
