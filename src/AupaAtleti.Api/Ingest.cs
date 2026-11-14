using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AupaAtleti.Api;

public class SyncOptions
{
    public int IntervalMinutes { get; set; } = 15;
    /// <summary>Only matches that kicked off this recently trigger a notification; older ones are backfilled silently.</summary>
    public int NotifyWindowHours { get; set; } = 72;
    /// <summary>How long to wait for the provider to publish stats before summarising without them.</summary>
    public int StatsGraceHours { get; set; } = 6;
    /// <summary>Pause between detail requests. The provider's burst limit is unpublished, so stay gentle.</summary>
    public int DetailDelayMs { get; set; } = 250;
}

public record SyncResult(int MatchesUpserted, int MatchesDetailed, int EventsEmitted);

public class MatchSync(AppDb db, PitchApiClient api, IOptions<PitchApiOptions> pitch, IOptions<SyncOptions> options, TimeProvider time, ILogger<MatchSync> log)
{
    public const string MatchFinished = "match.finished";

    // One sync at a time in this process: the timer and a manual "sync now" would otherwise race.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<SyncResult> RunAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await RunCoreAsync(ct); }
        finally { Gate.Release(); }
    }

    private async Task<SyncResult> RunCoreAsync(CancellationToken ct)
    {
        int upserted = 0, detailed = 0, emitted = 0;
        foreach (var leagueId in pitch.Value.LeagueIds)
            upserted += await UpsertLeagueAsync(leagueId, ct);

        var teamId = pitch.Value.TeamId;
        var pending = await db.Matches
            .Where(m => m.Status == "finished" && m.DetailsFetchedAt == null && (m.HomeTeamId == teamId || m.AwayTeamId == teamId))
            .OrderBy(m => m.KickoffUtc).ToListAsync(ct);
        foreach (var match in pending)
        {
            var outcome = await FetchDetailsAsync(match, ct);
            if (outcome is null) continue;
            detailed++;
            if (outcome.Value) emitted++;
        }
        log.LogInformation("Sync done: {Upserted} matches upserted, {Detailed} detailed, {Emitted} events", upserted, detailed, emitted);
        return new SyncResult(upserted, detailed, emitted);
    }

    /// <summary>Idempotent: matches are keyed by the provider's id, so running this twice changes nothing.</summary>
    private async Task<int> UpsertLeagueAsync(string leagueId, CancellationToken ct)
    {
        var league = await api.GetLeagueMatchesAsync(leagueId, ct);
        var existing = await db.Matches.Where(m => m.LeagueId == league.LeagueId && m.Season == league.Season).ToDictionaryAsync(m => m.Id, ct);
        foreach (var j in league.Matches)
        {
            var id = j.GetProperty("id").GetString()!;
            if (!existing.TryGetValue(id, out var m))
            {
                m = new Match
                {
                    Id = id, LeagueId = league.LeagueId, LeagueName = league.LeagueName, Season = league.Season,
                    HomeTeamId = "", HomeTeamName = "", AwayTeamId = "", AwayTeamName = "", Status = "",
                };
                db.Matches.Add(m);
            }
            m.HomeTeamId = j.GetProperty("home_team").GetProperty("id").GetString()!;
            m.HomeTeamName = j.GetProperty("home_team").GetProperty("name").GetString()!;
            m.AwayTeamId = j.GetProperty("away_team").GetProperty("id").GetString()!;
            m.AwayTeamName = j.GetProperty("away_team").GetProperty("name").GetString()!;
            m.KickoffUtc = j.GetProperty("time_utc").GetDateTimeOffset();
            m.Status = j.GetProperty("status").GetString()!;
            m.ScoreHome = Int(j, "score_home");
            m.ScoreAway = Int(j, "score_away");
        }
        await db.SaveChangesAsync(ct);
        return league.Matches.Count;
    }

    /// <summary>
    /// Downloads every document for a finished match, then stores them, the summary and the
    /// "match finished" event in ONE save. Either all of it is committed or none of it, so an
    /// event can never exist for a match we failed to store, nor a stored match miss its event.
    /// Returns null if skipped, otherwise whether an event was emitted.
    /// </summary>
    private async Task<bool?> FetchDetailsAsync(Match match, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var docs = new Dictionary<string, string>();
        foreach (var kind in PitchApiClient.DocumentPaths.Keys)
        {
            if (await api.GetMatchDocumentAsync(match.Id, kind, ct) is { } json) docs[kind] = json;
            await Task.Delay(options.Value.DetailDelayMs, ct);
        }
        if (!docs.ContainsKey("stats") && now - match.KickoffUtc < TimeSpan.FromHours(options.Value.StatsGraceHours))
        {
            log.LogInformation("Stats for {Match} not published yet, will retry next sync", match.Id);
            return null;
        }

        if (docs.TryGetValue("match", out var matchJson))
        {
            using var doc = JsonDocument.Parse(matchJson);
            var d = doc.RootElement.GetProperty("data");
            match.Round = Str(d, "round_name");
            match.Stadium = Str(d, "stadium");
            match.Referee = Str(d, "referee");
        }

        var stored = await db.MatchDocuments.Where(x => x.MatchId == match.Id).ToDictionaryAsync(x => x.Kind, ct);
        foreach (var (kind, json) in docs)
        {
            if (stored.TryGetValue(kind, out var row)) { row.Json = json; row.FetchedAt = now; }
            else db.MatchDocuments.Add(new MatchDocument { MatchId = match.Id, Kind = kind, Json = json, FetchedAt = now });
        }

        match.Summary = MatchSummary.Build(match, pitch.Value.TeamId, docs.GetValueOrDefault("stats"), docs.GetValueOrDefault("events"), docs.GetValueOrDefault("lineups"));
        match.DetailsFetchedAt = now;

        var emitted = now - match.KickoffUtc <= TimeSpan.FromHours(options.Value.NotifyWindowHours)
            && await PublishFinishedAsync(db, match, now, replay: false, ct);
        await db.SaveChangesAsync(ct);
        return emitted;
    }

    /// <summary>Queues the event without saving; see <see cref="EventPublisher.PublishAsync"/>.</summary>
    public static Task<bool> PublishFinishedAsync(AppDb db, Match m, DateTimeOffset now, bool replay, CancellationToken ct) =>
        EventPublisher.PublishAsync(db, MatchFinished,
            // A replay is a deliberate re-send, so it gets its own key instead of being deduplicated away.
            replay ? $"{MatchFinished}:{m.Id}:replay:{now.ToUnixTimeMilliseconds()}" : $"{MatchFinished}:{m.Id}",
            new
            {
                match_id = m.Id, league = m.LeagueName, round = m.Round,
                home = m.HomeTeamName, away = m.AwayTeamName, score_home = m.ScoreHome, score_away = m.ScoreAway,
                kickoff_utc = m.KickoffUtc, text = m.Summary,
            }, now, ct);

    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public class SyncWorker(IServiceScopeFactory scopes, IOptions<SyncOptions> options, ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MatchSync>().RunAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Sync failed, will retry next interval"); }
            await Task.Delay(TimeSpan.FromMinutes(options.Value.IntervalMinutes), ct);
        }
    }
}
