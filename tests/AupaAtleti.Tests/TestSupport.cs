using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AupaAtleti.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AupaAtleti.Tests;

/// <summary>Hand-written documents with the same shape as the provider's responses. No real data.</summary>
public static class Fixtures
{
    public const string Team = "t_home", Rival = "t_away", League = "l_test", MatchId = "m_1";

    public static Match Match() => new()
    {
        Id = MatchId, LeagueId = League, LeagueName = "LaLiga", Season = "2026/2027", Round = "12", Status = "finished",
        HomeTeamId = Team, HomeTeamName = "Rojiblancos", AwayTeamId = Rival, AwayTeamName = "Visitante FC", ScoreHome = 1, ScoreAway = 2,
    };

    public static string Stats(string xgHome, string xgAway) => $$$"""
        {"data":{"periods":[
          {"period":"FirstHalf","groups":[{"group_name":"Top stats","items":[{"key":"expected_goals","home":"9.99","away":"9.99"}]}]},
          {"period":"All","groups":[
            {"group_name":"Top stats","items":[
              {"key":"BallPossesion","home":"37","away":"63","format_type":"integer"},
              {"key":"expected_goals","home":"{{{xgHome}}}","away":"{{{xgAway}}}","format_type":"double"},
              {"key":"total_shots","home":"9","away":"19","format_type":"integer"},
              {"key":"accurate_passes","home":"317 (81%)","away":"615 (91%)","format_type":"integerWithPercentage"}]},
            {"group_name":"Shots","items":[{"key":"ShotsOnTarget","home":"4","away":"4","format_type":"integer"}]}]}]}}
        """;

    public static string Events() => $$$"""
        {"data":{"match_id":"{{{MatchId}}}","events":[
          {"event_type":"goal","minute":10,"minute_added":0,"team_id":"{{{Team}}}","player":{"name":"Delantero Uno"},"is_own_goal":false,"is_penalty":false},
          {"event_type":"Card","minute":30,"minute_added":0,"team_id":"{{{Team}}}","player":{"name":"Central Duro"}},
          {"event_type":"Goal","minute":45,"minute_added":2,"team_id":"{{{Rival}}}","player":{"name":"Rival Dos"},"is_own_goal":false,"is_penalty":true},
          {"event_type":"goal","minute":80,"minute_added":0,"team_id":"{{{Rival}}}","player":{"name":"Rival Tres"},"is_own_goal":false,"is_penalty":false}]}}
        """;

    public static string Lineups() => """{"data":{"home":{"formation":"4-4-2"},"away":{"formation":"4-3-3"}}}""";

    public static string MatchDetail() => $$$"""{"data":{"id":"{{{MatchId}}}","round_name":"12","stadium":"Estadio de Prueba","referee":"Árbitro Test"}}""";

    public static string LeagueMatches(DateTimeOffset kickoff, string status = "finished") => $$$"""
        {"data":{"league":{"id":"{{{League}}}","name":"LaLiga","season":"2026/2027"},"matches":[
          {"id":"{{{MatchId}}}","time_utc":"{{{kickoff:yyyy-MM-ddTHH:mm:ssZ}}}","status":"{{{status}}}",
           "home_team":{"id":"{{{Team}}}","name":"Rojiblancos"},"away_team":{"id":"{{{Rival}}}","name":"Visitante FC"},
           "score_home":{{{(status == "finished" ? "1" : "null")}}},"score_away":{{{(status == "finished" ? "2" : "null")}}} },
          {"id":"m_other","time_utc":"{{{kickoff:yyyy-MM-ddTHH:mm:ssZ}}}","status":"finished",
           "home_team":{"id":"t_x","name":"Equipo X"},"away_team":{"id":"t_y","name":"Equipo Y"},"score_home":0,"score_away":0}]}}
        """;
}

public class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Stands in for the data provider: serves whatever JSON the test registers for a path.</summary>
public class StubProvider : HttpMessageHandler
{
    public ConcurrentDictionary<string, string> Responses { get; } = new();
    public ConcurrentQueue<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery;
        Requests.Enqueue(path);
        return Task.FromResult(Responses.TryGetValue(path, out var json)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    public void ServeMatch(DateTimeOffset kickoff, bool withStats = true, string status = "finished")
    {
        Responses[$"/v1/leagues/{Fixtures.League}/matches?status=all"] = Fixtures.LeagueMatches(kickoff, status);
        Responses[$"/v1/matches/{Fixtures.MatchId}"] = Fixtures.MatchDetail();
        Responses[$"/v1/matches/{Fixtures.MatchId}/events"] = Fixtures.Events();
        Responses[$"/v1/matches/{Fixtures.MatchId}/lineups"] = Fixtures.Lineups();
        if (withStats) Responses[$"/v1/matches/{Fixtures.MatchId}/stats"] = Fixtures.Stats("1.53", "1.60");
    }
}

public record ReceivedWebhook(string Body, string Signature, string EventId);

/// <summary>A real HTTP server on localhost that plays the subscriber and records what it gets.</summary>
public sealed class Receiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();
    public int StatusCode { get; set; } = 200;
    public string Url { get; }

    private Receiver(WebApplication app, string url) { _app = app; Url = url; }

    public static async Task<Receiver> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var url = "";
        Receiver? self = null;
        app.MapPost("/hook", async (HttpRequest req) =>
        {
            using var reader = new StreamReader(req.Body);
            self!.Received.Enqueue(new(await reader.ReadToEndAsync(), req.Headers[WebhookSignature.Header].ToString(), req.Headers["X-Aupa-Event-Id"].ToString()));
            return Results.StatusCode(self.StatusCode);
        });
        await app.StartAsync();
        url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/hook";
        return self = new Receiver(app, url);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

/// <summary>The real app against a real PostgreSQL database (one per test class), with the provider stubbed and time under test control.</summary>
public class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string ApiKey = "test-key";
    public static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _connection =
        (Environment.GetEnvironmentVariable("TEST_DB") ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres")
        + $";Database=aupa_test_{Guid.NewGuid():N}";

    public StubProvider Provider { get; } = new();
    public FakeTime Time { get; } = new(Start);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Db", _connection);
        builder.UseSetting("ApiKey", ApiKey);
        builder.UseSetting("Workers:Enabled", "false"); // tests drive sync and delivery by hand, so nothing races
        builder.UseSetting("PitchApi:TeamId", Fixtures.Team);
        builder.UseSetting("PitchApi:LeagueIds", Fixtures.League);
        builder.UseSetting("Sync:DetailDelayMs", "0");
        builder.UseSetting("Webhooks:AllowPrivateNetworks", "true"); // the receiver runs on localhost
        builder.UseSetting("Webhooks:MaxAttempts", "3");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.AddHttpClient<PitchApiClient>().ConfigurePrimaryHttpMessageHandler(() => Provider);
        });
    }

    public HttpClient AdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        return client;
    }

    public async Task<T> ScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task<SyncResult> SyncAsync() => ScopeAsync(sp => sp.GetRequiredService<MatchSync>().RunAsync(default));
    public Task<int> DeliverAsync() => ScopeAsync(sp => sp.GetRequiredService<DeliveryService>().ProcessDueAsync(default));
    public Task<T> DbAsync<T>(Func<AppDb, Task<T>> query) => ScopeAsync(sp => query(sp.GetRequiredService<AppDb>()));

    public async Task<Subscription> SubscribeAsync(string url, SubscriptionFormat format = SubscriptionFormat.Generic)
    {
        var res = await AdminClient().PostAsync("/api/subscriptions",
            new StringContent(JsonSerializer.Serialize(new { url, format = format.ToString() }), Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        return await DbAsync(db => db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == id));
    }

    public Task InitializeAsync() { _ = Services; return Task.CompletedTask; } // starts the app, which creates and migrates the database

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DbAsync(async db => { await db.Database.EnsureDeletedAsync(); return 0; });
        await base.DisposeAsync();
    }
}
