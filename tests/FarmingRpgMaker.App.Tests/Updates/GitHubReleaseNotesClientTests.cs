using System.Net;
using FarmingRpgMaker.Updates;

namespace FarmingRpgMaker.App.Tests.Updates;

public sealed class GitHubReleaseNotesClientTests
{
    private static string SampleJson => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github-releases.json"));

    [Fact]
    public void ParseReleases_ReadsFields_AndDropsDrafts()
    {
        var releases = GitHubReleaseNotesClient.ParseReleases(SampleJson);

        Assert.Equal(["0.3.0-beta.1", "0.2.0", "0.1.0"], releases.Select(r => r.Version));
        var beta = releases[0];
        Assert.Equal("v0.3.0-beta.1", beta.TagName);
        Assert.Equal("Farming RPG Maker 0.3.0-beta.1", beta.Name);
        Assert.True(beta.Prerelease);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 10, 5, 0, TimeSpan.Zero), beta.PublishedAt);
        Assert.Equal("https://github.com/jxburros/farm-game-engine-native/releases/tag/v0.3.0-beta.1", beta.HtmlUrl);
        Assert.Contains("**mines**", beta.Body, StringComparison.Ordinal);
        Assert.Null(releases[2].Body);
        Assert.Null(releases[2].Name);
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("v0.2.0")]
    [InlineData("V0.2.0")]
    public void FindRelease_MatchesWithOrWithoutV(string version)
    {
        var release = GitHubReleaseNotesClient.FindRelease(GitHubReleaseNotesClient.ParseReleases(SampleJson), version);

        Assert.NotNull(release);
        Assert.StartsWith("## What's new", release.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void FindRelease_UnknownOrDraft_ReturnsNull()
    {
        var releases = GitHubReleaseNotesClient.ParseReleases(SampleJson);

        Assert.Null(GitHubReleaseNotesClient.FindRelease(releases, "9.9.9"));
        Assert.Null(GitHubReleaseNotesClient.FindRelease(releases, "0.4.0"));
    }

    [Fact]
    public async Task GetReleases_SendsUserAgentAndParses()
    {
        HttpRequestMessage? seen = null;
        var client = Client(request =>
        {
            seen = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleJson) };
        });

        var result = await client.GetReleasesAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Releases.Count);
        Assert.NotNull(seen);
        Assert.StartsWith(UpdateSource.ReleasesApiUrl, seen.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("FarmingRpgMaker", seen.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.Null(seen.Headers.Authorization);
    }

    [Fact]
    public async Task GetReleases_RateLimited_ReturnsFriendlyError()
    {
        var client = Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1790000000");
            return response;
        });

        var result = await client.GetReleasesAsync();

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Releases);
        Assert.Contains("limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetReleases_TooManyRequests_ReturnsFriendlyError()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await client.GetReleasesAsync();

        Assert.Contains("limit", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetReleases_Offline_ReturnsError()
    {
        var client = Client(_ => throw new HttpRequestException("No such host is known."));

        var result = await client.GetReleasesAsync();

        Assert.False(result.IsSuccess);
        Assert.Contains("offline", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetReleases_BadJson_ReturnsError()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>oops</html>") });

        var result = await client.GetReleasesAsync();

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task FindReleaseAsync_OnFailure_ReturnsNull()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Null(await client.FindReleaseAsync("0.2.0"));
    }

    private static GitHubReleaseNotesClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
