using System.Net;
using System.Text.Json;
using AupaAtleti.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AupaAtleti.Tests;

/// <summary>End to end against real PostgreSQL. xUnit builds a new instance per test, so each test gets its own database.</summary>
public class IntegrationTests : IAsyncLifetime
{
    private readonly AppFixture _app = new();
    private Receiver _receiver = null!;

    public async Task InitializeAsync()
    {
        await _app.InitializeAsync();
        _receiver = await Receiver.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _receiver.DisposeAsync();
        await ((IAsyncLifetime)_app).DisposeAsync();
    }

    private static DateTimeOffset Yesterday => AppFixture.Start.AddDays(-1);

    [Fact]
    public async Task Finished_match_is_stored_summarised_and_delivered_with_a_valid_signature()
    {
        var sub = await _app.SubscribeAsync(_receiver.Url);
        _app.Provider.ServeMatch(Yesterday);

        Assert.Equal(new SyncResult(2, 1, 1), await _app.SyncAsync());
        Assert.Equal(1, await _app.DeliverAsync());

        var match = await _app.DbAsync(db => db.Matches.SingleAsync(m => m.Id == Fixtures.MatchId));
        Assert.Equal("Estadio de Prueba", match.Stadium);
        Assert.Contains("Derrota del Rojiblancos en casa.", match.Summary);
        Assert.Equal(4, await _app.DbAsync(db => db.MatchDocuments.CountAsync())); // match, stats, lineups, events

        var hook = Assert.Single(_receiver.Received);
        Assert.True(WebhookSignature.Verify(sub.Secret, hook.Signature, hook.Body, _app.Time.Now, TimeSpan.FromMinutes(5)));
        var body = JsonDocument.Parse(hook.Body).RootElement;
        Assert.Equal("match.finished", body.GetProperty("type").GetString());
        Assert.Equal(Fixtures.MatchId, body.GetProperty("data").GetProperty("match_id").GetString());
        Assert.Equal(hook.EventId, body.GetProperty("id").GetString());

        var delivery = await _app.DbAsync(db => db.Deliveries.Include(d => d.Log).SingleAsync());
        Assert.Equal((DeliveryStatus.Succeeded, 1, 200), (delivery.Status, delivery.Attempts, delivery.Log.Single().StatusCode));
    }

    [Fact]
    public async Task Syncing_twice_never_duplicates_matches_events_or_deliveries()
    {
        await _app.SubscribeAsync(_receiver.Url);
        _app.Provider.ServeMatch(Yesterday);

        await _app.SyncAsync();
        Assert.Equal(new SyncResult(2, 0, 0), await _app.SyncAsync());
        await _app.DeliverAsync();
        await _app.DeliverAsync();

        Assert.Equal((2, 1, 1), await _app.DbAsync(async db => (await db.Matches.CountAsync(), await db.Events.CountAsync(), await db.Deliveries.CountAsync())));
        Assert.Single(_receiver.Received);
    }

    [Fact]
    public async Task Old_matches_are_backfilled_without_notifying()
    {
        await _app.SubscribeAsync(_receiver.Url);
        _app.Provider.ServeMatch(AppFixture.Start.AddDays(-30));

        Assert.Equal(new SyncResult(2, 1, 0), await _app.SyncAsync());
        Assert.NotNull((await _app.DbAsync(db => db.Matches.SingleAsync(m => m.Id == Fixtures.MatchId))).Summary);
        Assert.Equal(0, await _app.DbAsync(db => db.Events.CountAsync()));
    }

    [Fact]
    public async Task Waits_for_stats_that_are_not_published_yet_then_picks_them_up()
    {
        _app.Provider.ServeMatch(AppFixture.Start.AddHours(-2), withStats: false);
        Assert.Equal(new SyncResult(2, 0, 0), await _app.SyncAsync());

        _app.Provider.ServeMatch(AppFixture.Start.AddHours(-2), withStats: true);
        Assert.Equal(new SyncResult(2, 1, 1), await _app.SyncAsync());
        Assert.Contains("xG 1,53–1,60", (await _app.DbAsync(db => db.Matches.SingleAsync(m => m.Id == Fixtures.MatchId))).Summary);
    }

    [Fact]
    public async Task Upcoming_match_is_listed_and_not_summarised()
    {
        _app.Provider.ServeMatch(AppFixture.Start.AddDays(3), status: "not_started");
        Assert.Equal(new SyncResult(2, 0, 0), await _app.SyncAsync());

        var upcoming = JsonDocument.Parse(await _app.CreateClient().GetStringAsync("/api/matches?upcoming=true")).RootElement;
        Assert.Equal(Fixtures.MatchId, Assert.Single(upcoming.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Failed_delivery_is_retried_later_with_backoff_and_then_succeeds()
    {
        await _app.SubscribeAsync(_receiver.Url);
        _app.Provider.ServeMatch(Yesterday);
        await _app.SyncAsync();

        _receiver.StatusCode = 500;
        await _app.DeliverAsync();
        var failed = await _app.DbAsync(db => db.Deliveries.Include(d => d.Log).SingleAsync());
        Assert.Equal((DeliveryStatus.Pending, 1, "HTTP 500"), (failed.Status, failed.Attempts, failed.Log.Single().Error));
        Assert.InRange((failed.NextAttemptAt - _app.Time.Now).TotalSeconds, 48, 72); // 60s ±20%

        Assert.Equal(0, await _app.DeliverAsync()); // not due yet: nothing is claimed

        _receiver.StatusCode = 200;
        _app.Time.Now = _app.Time.Now.AddMinutes(2);
        Assert.Equal(1, await _app.DeliverAsync());

        var done = await _app.DbAsync(db => db.Deliveries.Include(d => d.Log).SingleAsync());
        Assert.Equal((DeliveryStatus.Succeeded, 2, 2), (done.Status, done.Attempts, done.Log.Count));
        // Both attempts carried the same event id: that is what lets a receiver drop duplicates.
        Assert.Single(_receiver.Received.Select(r => r.EventId).Distinct());
    }

    [Fact]
    public async Task Delivery_goes_dead_after_max_attempts_and_can_be_retried_by_hand()
    {
        await _app.SubscribeAsync(_receiver.Url);
        _app.Provider.ServeMatch(Yesterday);
        await _app.SyncAsync();

        _receiver.StatusCode = 503;
        for (var i = 0; i < 3; i++) // MaxAttempts is 3 in tests
        {
            await _app.DeliverAsync();
            _app.Time.Now = _app.Time.Now.AddHours(1);
        }
        var dead = await _app.DbAsync(db => db.Deliveries.SingleAsync());
        Assert.Equal((DeliveryStatus.Dead, 3), (dead.Status, dead.Attempts));
        Assert.Equal(0, await _app.DeliverAsync());

        _receiver.StatusCode = 200;
        Assert.Equal(HttpStatusCode.Accepted, (await _app.AdminClient().PostAsync($"/api/deliveries/{dead.Id}/retry", null)).StatusCode);
        Assert.Equal(1, await _app.DeliverAsync());
        Assert.Equal(DeliveryStatus.Succeeded, (await _app.DbAsync(db => db.Deliveries.SingleAsync())).Status);
    }

    [Fact]
    public async Task Unreachable_subscriber_is_recorded_as_a_failed_attempt()
    {
        await _app.SubscribeAsync("http://127.0.0.1:1/hook"); // nothing listens on port 1
        _app.Provider.ServeMatch(Yesterday);
        await _app.SyncAsync();
        await _app.DeliverAsync();

        var d = await _app.DbAsync(db => db.Deliveries.Include(x => x.Log).SingleAsync());
        Assert.Equal(DeliveryStatus.Pending, d.Status);
        Assert.Null(d.Log.Single().StatusCode);
        Assert.NotNull(d.Log.Single().Error);
    }

    [Fact]
    public async Task Discord_subscription_gets_the_summary_as_a_message()
    {
        await _app.SubscribeAsync(_receiver.Url, SubscriptionFormat.Discord);
        _app.Provider.ServeMatch(Yesterday);
        await _app.SyncAsync();
        await _app.DeliverAsync();

        var content = JsonDocument.Parse(Assert.Single(_receiver.Received).Body).RootElement.GetProperty("content").GetString();
        Assert.StartsWith("Rojiblancos 1-2 Visitante FC", content);
    }

    [Fact]
    public async Task Two_workers_claiming_at_once_never_get_the_same_delivery()
    {
        // 40 pending deliveries, then 4 workers claim 10 each at the same moment.
        await _app.DbAsync(async db =>
        {
            var sub = new Subscription { Id = Guid.NewGuid(), Url = _receiver.Url, Secret = "s", CreatedAt = AppFixture.Start };
            var ev = new OutboxEvent { Id = Guid.NewGuid(), Type = "t", DedupKey = "k", Payload = "{}", CreatedAt = AppFixture.Start };
            db.AddRange(sub, ev);
            db.AddRange(Enumerable.Range(0, 40).Select(_ => new Delivery { Id = Guid.NewGuid(), EventId = ev.Id, SubscriptionId = sub.Id, NextAttemptAt = AppFixture.Start }));
            return await db.SaveChangesAsync();
        });

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _app.ScopeAsync(sp => sp.GetRequiredService<DeliveryService>().ClaimAsync(default))));

        Assert.All(claims, c => Assert.Equal(10, c.Count));
        Assert.Equal(40, claims.SelectMany(c => c).Distinct().Count());
        // Leased rows are invisible to the next claim until the lease runs out…
        Assert.Empty(await _app.ScopeAsync(sp => sp.GetRequiredService<DeliveryService>().ClaimAsync(default)));
        // …and become claimable again afterwards, which is how a crashed worker's rows are recovered.
        _app.Time.Now = _app.Time.Now.AddMinutes(5);
        Assert.Equal(10, (await _app.ScopeAsync(sp => sp.GetRequiredService<DeliveryService>().ClaimAsync(default))).Count);
    }

    [Fact]
    public async Task Admin_endpoints_need_the_api_key_and_public_ones_do_not()
    {
        var anonymous = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/matches")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/standings")).StatusCode);

        var listed = await _app.AdminClient().GetStringAsync("/api/subscriptions");
        await _app.SubscribeAsync(_receiver.Url);
        Assert.DoesNotContain("secret", await _app.AdminClient().GetStringAsync("/api/subscriptions"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("[]", listed);
    }
}
