using System.Net;
using System.Net.Http;
using MW2Control.App;

internal static class ReleaseUpdateTests
{
    internal static async Task RunAsync()
    {
        var installed = new Version(1, 5, 0, 0);
        using var client = new HttpClient(new StubResponse(
            """{"tag_name":"v1.6","draft":false,"prerelease":false,"html_url":"https://example.test"}"""));
        var update = await ReleaseUpdates.CheckAsync(installed, CancellationToken.None, client);
        if (update?.Version != new Version(1, 6) ||
            update.Page.AbsoluteUri != "https://github.com/angelosmiliotis/mw2-control/releases/tag/v1.6")
            throw new Exception("A newer stable release must open its versioned GitHub page.");

        foreach (var payload in new[]
        {
            """{"tag_name":"v1.5","draft":false,"prerelease":false}""",
            """{"tag_name":"v1.4","draft":false,"prerelease":false}""",
            """{"tag_name":"v1.6","draft":false,"prerelease":true}""",
            """{"tag_name":"v1.6","draft":true,"prerelease":false}""",
            """{"tag_name":"v1.6-preview","draft":false,"prerelease":false}""",
            """{"tag_name":42,"draft":false,"prerelease":false}""",
            "[]",
            "{",
        })
        {
            using var invalid = new HttpClient(new StubResponse(payload));
            if (await ReleaseUpdates.CheckAsync(installed, CancellationToken.None, invalid) is not null)
                throw new Exception("Only a newer, published stable version should raise the update notice.");
        }

        using var unavailable = new HttpClient(new StubResponse("{}", HttpStatusCode.ServiceUnavailable));
        if (await ReleaseUpdates.CheckAsync(installed, CancellationToken.None, unavailable) is not null)
            throw new Exception("An unavailable update service must leave startup usable.");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        if (await ReleaseUpdates.CheckAsync(installed, canceled.Token, client) is not null)
            throw new Exception("Closing the app must cancel its update check.");

        Console.WriteLine("PASS: newer stable release detection, trusted link, and quiet failure");
    }

    private sealed class StubResponse(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri != ReleaseUpdates.Endpoint || request.Headers.UserAgent.Count == 0)
                throw new Exception("The update check must use the project release endpoint.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}
