# aupa-atleti

A personal Atlético de Madrid hub. It follows the team's matches, stores lineups and advanced
stats, writes a short summary when each match ends, and pushes that summary to my phone
through a small, reliable webhook engine.

Built with .NET 10, PostgreSQL and (soon) Angular.

```
PitchAPI ──▶ Ingest ──▶ PostgreSQL ──▶ outbox event ──▶ Delivery worker ──▶ Discord / any URL
            (every 15 min)   │                              retries, backoff, HMAC
                             └──▶ REST API: matches, standings, summaries
```

## What works today (phase 1)

- **Ingest.** Every 15 minutes it syncs LaLiga and Champions League fixtures and downloads
  lineups, events, team stats and advanced metrics for each finished Atlético match.
  Re-running it never creates duplicates.
- **Match summary.** A template-based summary in Spanish: result, scorers, xG, possession,
  shots, formation and one reading of the game (for example, "won despite creating less").
- **Standings.** The league table, computed from stored results.
- **Webhooks.** When a match ends, a `match.finished` event is delivered to every subscriber
  with at-least-once delivery, exponential backoff, signed payloads and a full attempt log.

Planned next: lineup editor (Angular), signing recommendations by position across 10 leagues,
and a private media gallery behind a feature flag.

## Run it

You need Docker and a free key from [pitchapi.dev](https://pitchapi.dev).

```bash
cp .env.example .env      # then fill in the two values
docker compose up --build
```

The API listens on `http://localhost:8080`. The first sync starts immediately and takes a
minute or two.

Without Docker: start PostgreSQL, then

```bash
export ConnectionStrings__Db="Host=localhost;Database=aupa;Username=postgres;Password=postgres"
export PitchApi__ApiKey=pk_live_...
export ApiKey=change-me
dotnet run --project src/AupaAtleti.Api
```

## Get match summaries on your phone (Discord)

1. In a Discord server you own: channel settings → Integrations → Webhooks → New webhook → copy the URL.
2. Register it:

```bash
curl -X POST http://localhost:8080/api/subscriptions \
  -H "X-Api-Key: $ADMIN_API_KEY" -H "Content-Type: application/json" \
  -d '{"url": "https://discord.com/api/webhooks/...", "format": "Discord"}'
```

3. Try it with a match that already finished (take an id from `GET /api/matches`):

```bash
curl -X POST http://localhost:8080/api/matches/MATCH_ID/notify -H "X-Api-Key: $ADMIN_API_KEY"
```

The Discord webhook URL is itself a secret: anyone who has it can post to your channel.

## API

Public, read-only:

| Method | Path | Returns |
|---|---|---|
| GET | `/api/matches` | Finished Atlético matches, newest first, with summary |
| GET | `/api/matches?upcoming=true` | Upcoming fixtures |
| GET | `/api/matches/{id}` | One match plus its stored lineups, events, stats and advanced metrics |
| GET | `/api/standings` | League table |
| GET | `/health` | Liveness |

Protected (send `X-Api-Key`):

| Method | Path | Does |
|---|---|---|
| POST | `/api/sync` | Run a sync now |
| POST | `/api/matches/{id}/notify` | Re-send the `match.finished` event for a match |
| POST | `/api/subscriptions` | Add a subscriber. Body: `url`, `format` (`Generic` or `Discord`). Returns the signing secret once |
| GET | `/api/subscriptions` | List subscribers |
| DELETE | `/api/subscriptions/{id}` | Remove a subscriber |
| GET | `/api/deliveries` | Last 100 deliveries with every attempt (status code, duration, error) |
| POST | `/api/deliveries/{id}/retry` | Retry a delivery that ran out of attempts |

## Receiving a webhook

`Generic` subscribers get:

```json
{ "id": "event id", "type": "match.finished", "created_at": "...", "data": { "match_id": "...", "home": "...", "text": "..." } }
```

with three headers: `X-Aupa-Event-Id`, `X-Aupa-Event-Type`, and
`X-Aupa-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body">`.

A receiver should:

1. Recompute the HMAC with its secret over `"{t}.{raw body}"` and compare in constant time.
2. Reject timestamps older than a few minutes.
3. Remember `X-Aupa-Event-Id` and ignore repeats. Delivery is at-least-once, so duplicates can happen.

`WebhookSignature.Verify` in [`Webhooks.cs`](src/AupaAtleti.Api/Webhooks.cs) is a reference implementation.

## Configuration

Everything is set through environment variables (`Section__Key`). Secrets never go in the repository.

| Variable | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Db` | local Postgres | Database |
| `PitchApi__ApiKey` | none | Data provider key |
| `ApiKey` | none | Admin key. If unset, protected endpoints reject everyone |
| `PitchApi__TeamId` | Atlético Madrid | Team to follow |
| `PitchApi__LeagueIds` | LaLiga, Champions League | Comma-separated league ids |
| `Sync__IntervalMinutes` | 15 | How often to sync |
| `Sync__NotifyWindowHours` | 72 | Older matches are backfilled without notifying |
| `Webhooks__MaxAttempts` | 8 | Attempts before a delivery is marked dead |
| `Webhooks__BaseDelaySeconds` | 60 | First retry delay; doubles each attempt, ±20% jitter |

## Tests

```bash
docker compose up -d db
dotnet test
```

46 tests. The integration tests run the real app against a real PostgreSQL database with the
data provider stubbed and the clock under test control. Set `TEST_DB` to point at a different server.

## Design notes

The reasoning behind the main decisions, and what was deliberately left out, is in
[docs/decisions.md](docs/decisions.md).

## Data

Match data comes from [PitchAPI](https://pitchapi.dev). This is a personal, non-commercial project
and is not affiliated with Club Atlético de Madrid.
