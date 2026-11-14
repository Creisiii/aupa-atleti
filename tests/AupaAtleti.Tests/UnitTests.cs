using System.Net;
using AupaAtleti.Api;

namespace AupaAtleti.Tests;

public class BackoffTests
{
    [Fact]
    public void Doubles_each_attempt_within_jitter_and_caps()
    {
        var o = new WebhookOptions { BaseDelaySeconds = 60, MaxDelaySeconds = 600 };
        var random = new Random(1);
        foreach (var (attempt, expected) in new[] { (1, 60.0), (2, 120.0), (3, 240.0), (4, 480.0), (5, 600.0), (9, 600.0) })
            for (var i = 0; i < 50; i++)
                Assert.InRange(Backoff.Delay(attempt, o, random).TotalSeconds, expected * 0.8, expected * 1.2);
    }
}

public class SignatureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void Valid_signature_verifies()
    {
        var header = WebhookSignature.Sign("secret", Now.ToUnixTimeSeconds(), "{\"a\":1}");
        Assert.True(WebhookSignature.Verify("secret", header, "{\"a\":1}", Now, Tolerance));
    }

    [Fact]
    public void Tampered_body_wrong_secret_or_stale_timestamp_fail()
    {
        var header = WebhookSignature.Sign("secret", Now.ToUnixTimeSeconds(), "{\"a\":1}");
        Assert.False(WebhookSignature.Verify("secret", header, "{\"a\":2}", Now, Tolerance));
        Assert.False(WebhookSignature.Verify("other", header, "{\"a\":1}", Now, Tolerance));
        Assert.False(WebhookSignature.Verify("secret", header, "{\"a\":1}", Now.AddMinutes(6), Tolerance));
        Assert.False(WebhookSignature.Verify("secret", "garbage", "{\"a\":1}", Now, Tolerance));
    }
}

public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1"), InlineData("10.1.2.3"), InlineData("172.16.0.1"), InlineData("172.31.255.255")]
    [InlineData("192.168.1.10"), InlineData("169.254.169.254"), InlineData("100.64.0.1"), InlineData("0.0.0.0")]
    [InlineData("::1"), InlineData("fe80::1"), InlineData("fd00::1"), InlineData("::ffff:10.0.0.1")]
    public void Private_addresses_are_blocked(string ip) => Assert.False(SsrfGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8"), InlineData("172.32.0.1"), InlineData("100.128.0.1"), InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string ip) => Assert.True(SsrfGuard.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://example.com/hook"), InlineData("https://user:pw@example.com/"), InlineData("https://127.0.0.1/")]
    [InlineData("https://[::1]/"), InlineData("ftp://example.com"), InlineData("not a url")]
    public void Unsafe_urls_are_rejected(string url) => Assert.NotNull(SsrfGuard.ValidateUrl(url, allowPrivate: false));

    [Fact]
    public void Https_public_url_is_accepted() => Assert.Null(SsrfGuard.ValidateUrl("https://discord.com/api/webhooks/1/abc", allowPrivate: false));
}

public class SummaryTests
{
    [Fact]
    public void Builds_full_summary_from_provider_documents()
    {
        var text = MatchSummary.Build(Fixtures.Match(), Fixtures.Team, Fixtures.Stats("1.53", "1.60"), Fixtures.Events(), Fixtures.Lineups());
        Assert.Equal("""
            Rojiblancos 1-2 Visitante FC · LaLiga, jornada 12
            Derrota del Rojiblancos en casa.
            Goles: Rojiblancos: Delantero Uno 10'; Visitante FC: Rival Dos 45+2' (p), Rival Tres 80'.
            Datos: xG 1,53–1,60 · posesión 37 %–63 % · tiros 9–19 (a puerta 4–4).
            Once del Rojiblancos: 4-4-2.
            Ocasiones parejas: el marcador se decidió en la definición.
            """.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Missing_documents_still_give_a_result_line()
    {
        var text = MatchSummary.Build(Fixtures.Match(), Fixtures.Team, null, null, null);
        Assert.Equal("Rojiblancos 1-2 Visitante FC · LaLiga, jornada 12\nDerrota del Rojiblancos en casa.", text.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(1, 0, 0.4, 1.9, "Ganó pese a")]
    [InlineData(0, 1, 2.1, 0.5, "Perdió aunque")]
    [InlineData(3, 3, 1.2, 1.3, "Pegada")]
    [InlineData(1, 1, 2.6, 2.7, "Faltó puntería")]
    [InlineData(2, 1, 1.5, 1.4, "Ocasiones parejas")]
    public void Insight_picks_the_first_matching_rule(int gf, int ga, double xgFor, double xgAgainst, string expected) =>
        Assert.StartsWith(expected, MatchSummary.Insight(gf, ga, xgFor, xgAgainst));

    [Fact]
    public void Insight_is_silent_when_nothing_stands_out() => Assert.Null(MatchSummary.Insight(1, 1, 1.1, 1.2));
}

public class StandingsTests
{
    private static Match M(string home, string away, int? sh, int? sa, string status = "finished") => new()
    {
        Id = $"{home}-{away}", LeagueId = "l", LeagueName = "L", Season = "s", Status = status,
        HomeTeamId = home, HomeTeamName = home, AwayTeamId = away, AwayTeamName = away, ScoreHome = sh, ScoreAway = sa,
    };

    [Fact]
    public void Orders_by_points_then_goal_difference_and_ignores_unplayed()
    {
        var table = Standings.Compute([M("A", "B", 3, 0), M("B", "C", 1, 1), M("C", "A", 2, 1), M("A", "C", null, null, "not_started")]);
        Assert.Equal(["C", "A", "B"], table.Select(r => r.Team));
        Assert.Equal((4, 1, 2), (table[0].Points, table[0].GoalDifference, table[0].Played));
        Assert.Equal((3, 2), (table[1].Points, table[1].GoalDifference));
        Assert.Equal((1, -3), (table[2].Points, table[2].GoalDifference));
    }
}
