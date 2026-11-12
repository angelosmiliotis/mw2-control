using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MW2Control.App;

internal sealed record ReleaseUpdate(Version Version, Uri Page);

internal static class ReleaseUpdates
{
    internal static readonly Uri Endpoint = new("https://api.github.com/repos/angelosmiliotis/mw2-control/releases/latest");
    private const string ReleasePage = "https://github.com/angelosmiliotis/mw2-control/releases/tag/";
    private static readonly HttpClient Client = new() { MaxResponseContentBufferSize = 512 * 1024 };

    internal static async Task<ReleaseUpdate?> CheckAsync(Version installed, CancellationToken cancellationToken, HttpClient? client = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.UserAgent.ParseAdd("MW2Control/" + installed.ToString(2));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await (client ?? Client).SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            var release = json.RootElement;
            if (release.ValueKind != JsonValueKind.Object ||
                !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
                !release.TryGetProperty("tag_name", out var tagProperty) || tagProperty.ValueKind != JsonValueKind.String)
                return null;
            var tag = tagProperty.GetString();
            if (tag is not { Length: > 0 and <= 40 } ||
                !Regex.IsMatch(tag, @"\Av?[0-9]+(\.[0-9]+){1,3}\z", RegexOptions.CultureInvariant) ||
                !Version.TryParse(tag.TrimStart('v'), out var latest) || Normalize(latest) <= Normalize(installed))
                return null;
            return new(latest, new Uri(ReleasePage + tag));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
