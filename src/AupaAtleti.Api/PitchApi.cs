using System.Net;
using System.Text.Json;

namespace AupaAtleti.Api;

public class PitchApiOptions
{
    public string BaseUrl { get; set; } = "https://api.pitchapi.dev";
    public string ApiKey { get; set; } = "";
    /// <summary>The team this app follows. Default: Atlético Madrid.</summary>
    public string TeamId { get; set; } = "t_3NIiXl";
    /// <summary>Comma-separated leagues to sync. Defaults: LaLiga, Champions League. The first one drives the standings.</summary>
    public string LeagueIds { get; set; } = "l_0ErfuF,l_0bfbkO";
    public string[] Leagues => LeagueIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>The only place that talks to the data provider. Swapping providers means replacing this file.</summary>
public class PitchApiClient(HttpClient http)
{
    /// <summary>Documents fetched for each finished match, keyed by the name they are stored under.</summary>
    public static readonly IReadOnlyDictionary<string, string> DocumentPaths = new Dictionary<string, string>
    {
        ["match"] = "",
        ["stats"] = "/stats",
        ["lineups"] = "/lineups",
        ["events"] = "/events",
        ["advanced"] = "/advanced",
        ["players"] = "/advanced/players",
    };

    public async Task<LeagueMatches> GetLeagueMatchesAsync(string leagueId, CancellationToken ct)
    {
        var json = await GetAsync($"/v1/leagues/{leagueId}/matches?status=all", ct)
            ?? throw new InvalidOperationException($"League {leagueId} not found");
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var league = data.GetProperty("league");
        return new LeagueMatches(
            league.GetProperty("id").GetString()!, league.GetProperty("name").GetString()!, league.GetProperty("season").GetString()!,
            data.GetProperty("matches").EnumerateArray().Select(m => m.Clone()).ToList());
    }

    /// <summary>Returns the raw JSON, or null when the provider has no such document for this match.</summary>
    public Task<string?> GetMatchDocumentAsync(string matchId, string kind, CancellationToken ct) =>
        GetAsync($"/v1/matches/{matchId}{DocumentPaths[kind]}", ct);

    private async Task<string?> GetAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var res = await http.GetAsync(path, ct);
            if (res.StatusCode == HttpStatusCode.NotFound) return null;
            // The provider publishes no rate limit, only a burst ceiling: back off as told, a few times.
            if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt), ct);
                continue;
            }
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync(ct);
        }
    }
}

public record LeagueMatches(string LeagueId, string LeagueName, string Season, List<JsonElement> Matches);
