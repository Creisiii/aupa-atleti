using System.Security.Cryptography;
using System.Text;
using AupaAtleti.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(config.GetConnectionString("Db")));
builder.Services.Configure<PitchApiOptions>(config.GetSection("PitchApi"));
builder.Services.Configure<SyncOptions>(config.GetSection("Sync"));
builder.Services.Configure<WebhookOptions>(config.GetSection("Webhooks"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddHttpClient<PitchApiClient>((sp, http) =>
{
    var o = sp.GetRequiredService<IOptions<PitchApiOptions>>().Value;
    http.BaseAddress = new Uri(o.BaseUrl);
    http.DefaultRequestHeaders.Add("X-API-KEY", o.ApiKey);
    http.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient("webhooks")
    .ConfigurePrimaryHttpMessageHandler(sp => SsrfGuard.CreateHandler(sp.GetRequiredService<IOptions<WebhookOptions>>().Value.AllowPrivateNetworks));

builder.Services.AddScoped<MatchSync>();
builder.Services.AddScoped<DeliveryService>();
if (config.GetValue("Workers:Enabled", true))
{
    builder.Services.AddHostedService<SyncWorker>();
    builder.Services.AddHostedService<DeliveryWorker>();
}

var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDb>().Database.MigrateAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---- Public, read-only ----

app.MapGet("/api/matches", async (AppDb db, IOptions<PitchApiOptions> pitch, bool? upcoming) =>
{
    var teamId = pitch.Value.TeamId;
    var q = db.Matches.AsNoTracking().Where(m => m.HomeTeamId == teamId || m.AwayTeamId == teamId);
    q = upcoming == true
        ? q.Where(m => m.Status != "finished").OrderBy(m => m.KickoffUtc)
        : q.Where(m => m.Status == "finished").OrderByDescending(m => m.KickoffUtc);
    return await q.Take(60).ToListAsync();
});

app.MapGet("/api/matches/{id}", async (AppDb db, string id) =>
{
    var match = await db.Matches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
    if (match is null) return Results.NotFound();
    var docs = await db.MatchDocuments.AsNoTracking().Where(d => d.MatchId == id).ToListAsync();
    // The stored documents are already JSON: embed them as-is instead of re-encoding them as strings.
    return Results.Ok(new { match, documents = docs.ToDictionary(d => d.Kind, d => System.Text.Json.JsonDocument.Parse(d.Json).RootElement.GetProperty("data")) });
});

app.MapGet("/api/standings", async (AppDb db, IOptions<PitchApiOptions> pitch, string? league) =>
{
    var leagueId = league ?? pitch.Value.Leagues[0];
    var season = await db.Matches.Where(m => m.LeagueId == leagueId).MaxAsync(m => (string?)m.Season);
    var matches = await db.Matches.AsNoTracking().Where(m => m.LeagueId == leagueId && m.Season == season).ToListAsync();
    return Standings.Compute(matches).Select((r, i) => new { position = i + 1, r.Team, r.Played, r.Won, r.Drawn, r.Lost, r.GoalsFor, r.GoalsAgainst, r.GoalDifference, r.Points });
});

// ---- Protected: require the X-Api-Key header ----

var admin = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
{
    // Fails closed: with no key configured, nobody gets in.
    var expected = Encoding.UTF8.GetBytes(config["ApiKey"] ?? "");
    var given = Encoding.UTF8.GetBytes(ctx.HttpContext.Request.Headers["X-Api-Key"].ToString());
    return expected.Length > 0 && CryptographicOperations.FixedTimeEquals(expected, given)
        ? await next(ctx)
        : Results.Unauthorized();
});

admin.MapPost("/sync", (MatchSync sync, CancellationToken ct) => sync.RunAsync(ct));

admin.MapPost("/matches/{id}/notify", async (AppDb db, TimeProvider time, string id, CancellationToken ct) =>
{
    var match = await db.Matches.SingleOrDefaultAsync(m => m.Id == id, ct);
    if (match?.Summary is null) return Results.NotFound();
    await MatchSync.PublishFinishedAsync(db, match, time.GetUtcNow(), replay: true, ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted();
});

admin.MapPost("/subscriptions", async (AppDb db, TimeProvider time, IOptions<WebhookOptions> o, SubscriptionRequest req) =>
{
    if (SsrfGuard.ValidateUrl(req.Url, o.Value.AllowPrivateNetworks) is { } error) return Results.BadRequest(new { error });
    var sub = new Subscription
    {
        Id = Guid.NewGuid(), Url = req.Url, Format = req.Format, CreatedAt = time.GetUtcNow(),
        Secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
    };
    db.Subscriptions.Add(sub);
    await db.SaveChangesAsync();
    // The secret is shown once, here. It is never returned again.
    return Results.Created($"/api/subscriptions/{sub.Id}", new { sub.Id, sub.Url, sub.Format, sub.Secret });
});

admin.MapGet("/subscriptions", async (AppDb db) =>
    await db.Subscriptions.AsNoTracking().OrderBy(s => s.CreatedAt)
        .Select(s => new { s.Id, s.Url, s.Format, s.Active, s.ConsecutiveFailures, s.CreatedAt }).ToListAsync());

admin.MapDelete("/subscriptions/{id:guid}", async (AppDb db, Guid id) =>
    await db.Subscriptions.Where(s => s.Id == id).ExecuteDeleteAsync() == 1 ? Results.NoContent() : Results.NotFound());

admin.MapGet("/deliveries", async (AppDb db) =>
    await db.Deliveries.AsNoTracking().OrderByDescending(d => d.Event.CreatedAt).Take(100)
        .Select(d => new
        {
            d.Id, d.EventId, EventType = d.Event.Type, d.SubscriptionId, Status = d.Status.ToString(), d.Attempts, d.NextAttemptAt, d.CompletedAt,
            Log = d.Log.OrderBy(a => a.At).Select(a => new { a.At, a.StatusCode, a.DurationMs, a.Error }),
        }).ToListAsync());

admin.MapPost("/deliveries/{id:guid}/retry", async (AppDb db, TimeProvider time, Guid id) =>
{
    var now = time.GetUtcNow();
    var updated = await db.Deliveries.Where(d => d.Id == id && d.Status == DeliveryStatus.Dead)
        .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DeliveryStatus.Pending).SetProperty(d => d.Attempts, 0)
            .SetProperty(d => d.NextAttemptAt, now).SetProperty(d => d.CompletedAt, (DateTimeOffset?)null));
    return updated == 1 ? Results.Accepted() : Results.NotFound();
});

app.Run();

public record SubscriptionRequest(string Url, SubscriptionFormat Format = SubscriptionFormat.Generic);

public partial class Program;
