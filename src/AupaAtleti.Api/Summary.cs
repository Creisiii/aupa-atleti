using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AupaAtleti.Api;

/// <summary>Builds the post-match summary from templates. Pure function: same inputs, same text.</summary>
public static class MatchSummary
{
    public static string Build(Match m, string teamId, string? statsJson, string? eventsJson, string? lineupsJson)
    {
        var isHome = m.HomeTeamId == teamId;
        var team = isHome ? m.HomeTeamName : m.AwayTeamName;
        int goalsFor = (isHome ? m.ScoreHome : m.ScoreAway) ?? 0, goalsAgainst = (isHome ? m.ScoreAway : m.ScoreHome) ?? 0;

        var sb = new StringBuilder();
        sb.Append($"{m.HomeTeamName} {m.ScoreHome}-{m.ScoreAway} {m.AwayTeamName} · {m.LeagueName}");
        if (m.Round is not null) sb.Append(int.TryParse(m.Round, out _) ? $", jornada {m.Round}" : $", {m.Round}");
        sb.AppendLine();

        var result = goalsFor > goalsAgainst ? "Victoria" : goalsFor < goalsAgainst ? "Derrota" : "Empate";
        sb.AppendLine($"{result} del {team} {(isHome ? "en casa" : "fuera")}.");

        var goals = Goals(eventsJson, m);
        if (goals.Length > 0) sb.AppendLine($"Goles: {goals}.");

        var stats = new Stats(statsJson);
        var line = new List<string>();
        if (stats.Pair("expected_goals") is var (xh, xa)) line.Add($"xG {Num(xh)}–{Num(xa)}");
        if (stats.Pair("BallPossesion") is var (ph, pa)) line.Add($"posesión {ph:0} %–{pa:0} %");
        if (stats.Pair("total_shots") is var (th, ta))
        {
            var onTarget = stats.Pair("ShotsOnTarget") is var (oh, oa) ? $" (a puerta {oh:0}–{oa:0})" : "";
            line.Add($"tiros {th:0}–{ta:0}{onTarget}");
        }
        if (line.Count > 0) sb.AppendLine($"Datos: {string.Join(" · ", line)}.");

        if (Formation(lineupsJson, isHome) is { } formation) sb.AppendLine($"Once del {team}: {formation}.");

        if (stats.Pair("expected_goals") is var (xgHome, xgAway))
        {
            var (xgFor, xgAgainst) = isHome ? (xgHome, xgAway) : (xgAway, xgHome);
            if (Insight(goalsFor, goalsAgainst, xgFor, xgAgainst) is { } insight) sb.AppendLine(insight);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One reading of the match from goals vs expected goals. First rule that applies wins.</summary>
    public static string? Insight(int goalsFor, int goalsAgainst, double xgFor, double xgAgainst)
    {
        if (goalsFor > goalsAgainst && xgFor < xgAgainst) return "Ganó pese a generar menos peligro que el rival.";
        if (goalsFor < goalsAgainst && xgFor > xgAgainst) return "Perdió aunque generó más peligro que el rival.";
        if (goalsFor - xgFor >= 1) return $"Pegada: {goalsFor} goles con solo {Num(xgFor)} de xG.";
        if (xgFor - goalsFor >= 1) return $"Faltó puntería: {Num(xgFor)} de xG para {goalsFor} {(goalsFor == 1 ? "gol" : "goles")}.";
        if (goalsFor != goalsAgainst && Math.Abs(xgFor - xgAgainst) < 0.3) return "Ocasiones parejas: el marcador se decidió en la definición.";
        return null;
    }

    private static string Goals(string? eventsJson, Match m)
    {
        if (eventsJson is null) return "";
        using var doc = JsonDocument.Parse(eventsJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("events", out var events)) return "";
        var byTeam = new Dictionary<string, List<string>>();
        foreach (var e in events.EnumerateArray())
        {
            // The provider's casing of event_type is not consistent ("goal" / "Goal").
            if (!string.Equals(e.GetProperty("event_type").GetString(), "goal", StringComparison.OrdinalIgnoreCase)) continue;
            var teamId = e.GetProperty("team_id").GetString() ?? "";
            var added = e.TryGetProperty("minute_added", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
            var text = $"{e.GetProperty("player").GetProperty("name").GetString()} {e.GetProperty("minute").GetInt32()}{(added > 0 ? $"+{added}" : "")}'";
            if (Flag(e, "is_penalty")) text += " (p)";
            if (Flag(e, "is_own_goal")) text += " (p.p.)";
            if (!byTeam.TryGetValue(teamId, out var list)) byTeam[teamId] = list = [];
            list.Add(text);
        }
        return string.Join("; ", byTeam.Select(kv =>
            $"{(kv.Key == m.HomeTeamId ? m.HomeTeamName : m.AwayTeamName)}: {string.Join(", ", kv.Value)}"));
    }

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Formation(string? lineupsJson, bool isHome)
    {
        if (lineupsJson is null) return null;
        using var doc = JsonDocument.Parse(lineupsJson);
        return doc.RootElement.TryGetProperty("data", out var d)
            && d.TryGetProperty(isHome ? "home" : "away", out var side)
            && side.TryGetProperty("formation", out var f) ? f.GetString() : null;
    }

    private static string Num(double v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>Reads home/away pairs from the provider's full-match stats, which arrive as display strings.</summary>
    private sealed class Stats
    {
        private readonly Dictionary<string, (double, double)> _values = [];

        public Stats(string? json)
        {
            if (json is null) return;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("periods", out var periods)) return;
            foreach (var period in periods.EnumerateArray())
            {
                if (period.GetProperty("period").GetString() != "All") continue;
                foreach (var group in period.GetProperty("groups").EnumerateArray())
                    foreach (var item in group.GetProperty("items").EnumerateArray())
                        if (Parse(item, "home") is { } h && Parse(item, "away") is { } a)
                            _values.TryAdd(item.GetProperty("key").GetString() ?? "", (h, a));
            }
        }

        public (double Home, double Away)? Pair(string key) => _values.TryGetValue(key, out var v) ? v : null;

        // Values look like "37", "1.53" or "317 (81%)": the leading number is the one we want.
        private static double? Parse(JsonElement item, string side)
        {
            var raw = item.TryGetProperty(side, out var v) ? v.ToString() : "";
            var first = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
    }
}

public record StandingRow(string TeamId, string Team, int Played, int Won, int Drawn, int Lost, int GoalsFor, int GoalsAgainst)
{
    public int Points => Won * 3 + Drawn;
    public int GoalDifference => GoalsFor - GoalsAgainst;
}

public static class Standings
{
    public static List<StandingRow> Compute(IEnumerable<Match> matches)
    {
        var rows = new Dictionary<string, StandingRow>();
        void Add(string id, string name, int gf, int ga)
        {
            var r = rows.GetValueOrDefault(id) ?? new StandingRow(id, name, 0, 0, 0, 0, 0, 0);
            rows[id] = r with
            {
                Played = r.Played + 1,
                Won = r.Won + (gf > ga ? 1 : 0),
                Drawn = r.Drawn + (gf == ga ? 1 : 0),
                Lost = r.Lost + (gf < ga ? 1 : 0),
                GoalsFor = r.GoalsFor + gf,
                GoalsAgainst = r.GoalsAgainst + ga,
            };
        }
        foreach (var m in matches.Where(m => m.IsFinished && m.ScoreHome is not null && m.ScoreAway is not null))
        {
            Add(m.HomeTeamId, m.HomeTeamName, m.ScoreHome!.Value, m.ScoreAway!.Value);
            Add(m.AwayTeamId, m.AwayTeamName, m.ScoreAway!.Value, m.ScoreHome!.Value);
        }
        // ponytail: ties broken by goal difference then goals scored. LaLiga's official tiebreak is
        // head-to-head first; add it if two teams level on points ever matters to you.
        return rows.Values
            .OrderByDescending(r => r.Points).ThenByDescending(r => r.GoalDifference)
            .ThenByDescending(r => r.GoalsFor).ThenBy(r => r.Team, StringComparer.Ordinal)
            .ToList();
    }
}
