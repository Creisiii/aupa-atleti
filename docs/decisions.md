# Design decisions

Why the system looks the way it does, what was left out on purpose, and where each decision
lives in the code.

## 1. One deployable, not microservices

The API and both background workers run in a single process, organised by file:
`Ingest.cs`, `Webhooks.cs`, `Summary.cs`, `PitchApi.cs`, `Data.cs`.

**Why.** One user, a few events a week. Separate services would add network hops, deployment
work and failure modes without solving any problem this system has. The seams are still there:
the delivery worker only talks to the database, so it could be moved to its own process
without touching the rest.

## 2. PostgreSQL is the queue

Pending deliveries are rows. Workers claim them with:

```sql
UPDATE "Deliveries" SET "LockedUntil" = now() + lease
WHERE "Id" IN (SELECT "Id" FROM "Deliveries" WHERE due … FOR UPDATE SKIP LOCKED LIMIT n)
RETURNING "Id"
```

**Why.** `SKIP LOCKED` lets several workers poll at once and each receive different rows, with
no blocking. No RabbitMQ or Kafka to run, and, more importantly, the queue shares a transaction
with the data (see 3).

**Lease instead of a held transaction.** The claim commits immediately and marks the rows as
leased for two minutes. The HTTP call then happens outside any transaction. If the worker dies
mid-send, the lease expires and another worker picks the row up.

**Ceiling.** Fine up to thousands of deliveries per minute. Beyond that, polling and row churn
become the bottleneck and a real broker earns its place.

Code: `DeliveryService.ClaimAsync`. Test: `Two_workers_claiming_at_once_never_get_the_same_delivery`.

## 3. Outbox: the event commits with the data

When a finished match is stored, its documents, its summary, the `match.finished` event and
one delivery per subscriber are written in a single `SaveChanges`, which is a single transaction.

**Why.** The alternative, "save the match, then publish", has a gap: crash between the two and
the match is stored but nobody is ever told. With the outbox either everything is committed or
nothing is.

Code: `MatchSync.FetchDetailsAsync`, `EventPublisher.PublishAsync`.

## 4. At-least-once delivery, and why not exactly-once

If a subscriber receives a webhook but its `200 OK` is lost, we cannot tell that apart from a
failure, so we retry. The subscriber may therefore see the same event twice. Exactly-once
delivery over a network is not achievable; what is achievable is at-least-once delivery plus an
idempotent receiver. Every attempt carries the same `X-Aupa-Event-Id` so the receiver can drop
repeats.

## 5. Retries: exponential backoff with jitter

Delay doubles per attempt (1, 2, 4… minutes), capped at 6 hours, ±20% random jitter. After 8
attempts the delivery is marked `Dead` and can be retried by hand.

**Why backoff.** A subscriber that is down needs time, not more traffic.
**Why jitter.** Without it, everything that failed together retries together, in synchronised
waves that can knock a recovering service over again.

A subscription is switched off after 20 failed attempts in a row.

Code: `Backoff.Delay`. Test: `Failed_delivery_is_retried_later_with_backoff_and_then_succeeds`.

## 6. Signed payloads

Each request carries `X-Aupa-Signature: t=<timestamp>,v1=<HMAC-SHA256("t.body")>`, using a
per-subscription secret that is shown once at creation.

**Why sign.** The receiver's URL is public. The signature proves the request came from this
system and was not altered.
**Why include the timestamp in the signed text.** Otherwise a captured request could be
replayed forever. The receiver rejects old timestamps.

## 7. SSRF protection

Subscribers choose the URL the server calls. Without checks they could aim it at `localhost`,
the cloud metadata address or internal services.

Three layers:
1. On registration: `https` only, no embedded credentials, no private IP literals.
2. **At connect time**, the handler resolves the hostname itself and refuses private addresses.
   This is the one that matters: a hostname can resolve to a public address when registered and
   a private one later (DNS rebinding), so validating only at registration is not enough.
3. Redirects are not followed, since a redirect could point somewhere private after the check.

Code: `SsrfGuard`. Tests: `SsrfGuardTests`.

## 8. Idempotent ingest

Matches are keyed by the provider's id and upserted. Documents are keyed by (match, kind).
The event has a unique `DedupKey` (`match.finished:<match id>`). Running the sync twice, or
crashing halfway and running it again, converges on the same state.

Test: `Syncing_twice_never_duplicates_matches_events_or_deliveries`.

## 9. Keep the provider's raw response

Every document is stored verbatim as `jsonb` next to the derived fields.

**Why.** If a summary rule changes, summaries can be rebuilt from what is stored, with no API
calls. It is also the insurance policy against the provider: PitchAPI is free, publishes no
terms of use and offers no guarantees, so the data already collected must outlive it.

## 10. One file talks to the provider

Only `PitchApi.cs` knows the provider's URLs. Replacing the provider means replacing that file
and the JSON field names in `Ingest.cs` and `Summary.cs`.

No `IMatchDataProvider` interface yet: with one implementation it would be an abstraction over
nothing. It gets extracted the day a second provider exists.

## 11. Edge cases found against real data

- **Backfill does not spam.** On first run the season's past matches are stored silently; only
  matches from the last 72 hours trigger a notification.
- **Stats arrive late.** Right after the final whistle the provider may not have stats yet.
  The sync waits up to 6 hours before summarising without them.
- **Inconsistent casing.** The provider documents `"Goal"` and returns `"goal"`. Compared
  case-insensitively.
- **Numbers as display strings.** Stats come as `"317 (81%)"`; the parser takes the leading number.

## Left out on purpose

| Not built | Why | Add it when |
|---|---|---|
| Message broker | Postgres does the job at this volume | Throughput outgrows polling |
| User accounts, OAuth | One user; a single admin key is enough | There is a second user |
| Provider interface | One provider | A second provider is added |
| Head-to-head tiebreak in standings | Goal difference is right almost always | Two teams level on points matters |
| LLM-written summaries | Templates are free, deterministic and testable | The templates feel repetitive |
| Live scores | The provider is post-match only | A live data source is available |

## Questions worth being ready for

**What happens if the process crashes right after sending a webhook but before recording it?**
The lease expires, the delivery is claimed again and sent a second time. That is the
at-least-once guarantee; the stable event id lets the receiver deduplicate.

**Why not hold the row lock for the duration of the HTTP call?**
It would pin a database connection and an open transaction for up to the 10-second timeout per
delivery. Leases keep transactions short.

**How would you scale it?**
Run more copies of the process: `SKIP LOCKED` already makes the workers safe to run in
parallel. Next, partition or archive finished deliveries. Past that, move delivery to a broker.

**What if the data provider disappears?**
The app keeps serving everything already stored. Only `PitchApi.cs` and the field mappings
need rewriting for a new provider.

**How do you know the retry logic works?**
The tests control the clock and run a real HTTP receiver that can be told to fail, so they
assert the actual schedule: first failure, not re-sent early, re-sent after the delay, success.
