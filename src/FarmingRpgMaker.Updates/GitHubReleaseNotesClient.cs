using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmingRpgMaker.Updates;

/// <summary>One entry of the GitHub "list releases" API.</summary>
public sealed record GitHubReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Release notes as Markdown.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    /// <summary>The tag without a leading <c>v</c> (<c>v1.2.3</c> → <c>1.2.3</c>).</summary>
    [JsonIgnore]
    public string Version => GitHubReleaseNotesClient.NormalizeVersion(TagName);
}

/// <summary>Result of <see cref="GitHubReleaseNotesClient.GetReleasesAsync"/>: releases, or a user-facing error.</summary>
public sealed record GitHubReleasesResult(IReadOnlyList<GitHubReleaseInfo> Releases, string? Error)
{
    public bool IsSuccess => Error is null;
}

/// <summary>
/// Reads release metadata (notes, dates, links) from the public GitHub API without a
/// token. Unauthenticated requests are limited to 60/hour per IP, so failures —
/// rate limits, offline, DNS — are returned as messages, never thrown.
/// </summary>
public sealed class GitHubReleaseNotesClient
{
    private readonly HttpClient _http;
    private readonly string _releasesApiUrl;

    public GitHubReleaseNotesClient(HttpClient? httpClient = null, string releasesApiUrl = UpdateSource.ReleasesApiUrl)
    {
        _http = httpClient ?? SharedClient.Value;
        _releasesApiUrl = releasesApiUrl;
    }

    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(20) });

    /// <summary>Fetches the most recent releases (drafts excluded), newest first.</summary>
    public async Task<GitHubReleasesResult> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesApiUrl + "?per_page=30");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("FarmingRpgMaker", SanitizeForHeader(AppVersion.Current)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubReleasesResult([], DescribeFailure(response));
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new GitHubReleasesResult(ParseReleases(json), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new GitHubReleasesResult([], "GitHub did not respond in time. Check your internet connection and try again.");
        }
        catch (HttpRequestException ex)
        {
            return new GitHubReleasesResult([], $"Couldn't reach GitHub ({ex.Message}). Are you offline?");
        }
        catch (JsonException)
        {
            return new GitHubReleasesResult([], "GitHub returned an unexpected response.");
        }
    }

    /// <summary>Finds the release for <paramref name="version"/> (with or without a leading <c>v</c>); null if unavailable.</summary>
    public async Task<GitHubReleaseInfo?> FindReleaseAsync(string version, CancellationToken cancellationToken = default)
    {
        var result = await GetReleasesAsync(cancellationToken).ConfigureAwait(false);
        return FindRelease(result.Releases, version);
    }

    public static GitHubReleaseInfo? FindRelease(IEnumerable<GitHubReleaseInfo> releases, string version)
    {
        var wanted = NormalizeVersion(version);
        return releases.FirstOrDefault(r => string.Equals(r.Version, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses the JSON array returned by <c>GET /repos/{owner}/{repo}/releases</c>. Drafts are dropped.</summary>
    public static IReadOnlyList<GitHubReleaseInfo> ParseReleases(string json)
    {
        var releases = JsonSerializer.Deserialize<List<GitHubReleaseInfo>>(json) ?? [];
        return releases.Where(r => !r.Draft && !string.IsNullOrWhiteSpace(r.TagName)).ToList();
    }

    public static string NormalizeVersion(string tagOrVersion)
    {
        var trimmed = tagOrVersion.Trim();
        return trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V') && char.IsDigit(trimmed[1])
            ? trimmed[1..]
            : trimmed;
    }

    internal static string DescribeFailure(HttpResponseMessage response)
    {
        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) ? values.FirstOrDefault() : null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden && remaining == "0"))
        {
            var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
                && long.TryParse(resetValues.FirstOrDefault(), out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime()
                : (DateTimeOffset?)null;
            return reset is { } r
                ? $"GitHub's hourly request limit was reached. Try again after {r:t}."
                : "GitHub's hourly request limit was reached. Try again later.";
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return "No releases were found on GitHub yet.";
        }

        return $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private static string SanitizeForHeader(string version)
    {
        var chars = version.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').ToArray();
        return chars.Length == 0 ? "0" : new string(chars);
    }
}
