using Microsoft.EntityFrameworkCore;

namespace AupaAtleti.Api;

public class Match
{
    public required string Id { get; set; }            // provider id, e.g. m_1FwCGv
    public required string LeagueId { get; set; }
    public required string LeagueName { get; set; }
    public required string Season { get; set; }
    public string? Round { get; set; }
    public required string HomeTeamId { get; set; }
    public required string HomeTeamName { get; set; }
    public required string AwayTeamId { get; set; }
    public required string AwayTeamName { get; set; }
    public DateTimeOffset KickoffUtc { get; set; }
    public required string Status { get; set; }        // finished | not_started | ...
    public int? ScoreHome { get; set; }
    public int? ScoreAway { get; set; }
    public string? Stadium { get; set; }
    public string? Referee { get; set; }
    public DateTimeOffset? DetailsFetchedAt { get; set; }
    public string? Summary { get; set; }

    public bool IsFinished => Status == "finished";
    public bool Involves(string teamId) => HomeTeamId == teamId || AwayTeamId == teamId;
}

/// <summary>Provider response stored verbatim, so derived data can be rebuilt without calling the API again.</summary>
public class MatchDocument
{
    public required string MatchId { get; set; }
    public required string Kind { get; set; }           // match | stats | lineups | events | advanced | players
    public required string Json { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

public enum SubscriptionFormat { Generic, Discord }

public class Subscription
{
    public Guid Id { get; set; }
    public required string Url { get; set; }
    public required string Secret { get; set; }
    public SubscriptionFormat Format { get; set; }
    public bool Active { get; set; } = true;
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class OutboxEvent
{
    public Guid Id { get; set; }
    public required string Type { get; set; }           // e.g. match.finished
    public required string DedupKey { get; set; }       // unique: the same fact is never recorded twice
    public required string Payload { get; set; }        // JSON
    public DateTimeOffset CreatedAt { get; set; }
}

public enum DeliveryStatus { Pending, Succeeded, Dead }

public class Delivery
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public OutboxEvent Event { get; set; } = null!;
    public Guid SubscriptionId { get; set; }
    public Subscription Subscription { get; set; } = null!;
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }    // lease held by the worker that claimed it
    public DateTimeOffset? CompletedAt { get; set; }
    public List<DeliveryAttempt> Log { get; set; } = [];
}

public class DeliveryAttempt
{
    public long Id { get; set; }
    public Guid DeliveryId { get; set; }
    public DateTimeOffset At { get; set; }
    public int? StatusCode { get; set; }
    public int DurationMs { get; set; }
    public string? Error { get; set; }
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchDocument> MatchDocuments => Set<MatchDocument>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<OutboxEvent> Events => Set<OutboxEvent>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Match>().HasIndex(m => new { m.LeagueId, m.Season });
        b.Entity<Match>().HasIndex(m => m.KickoffUtc);
        b.Entity<MatchDocument>().HasKey(d => new { d.MatchId, d.Kind });
        b.Entity<MatchDocument>().Property(d => d.Json).HasColumnType("jsonb");
        b.Entity<OutboxEvent>().HasIndex(e => e.DedupKey).IsUnique();
        b.Entity<OutboxEvent>().Property(e => e.Payload).HasColumnType("jsonb");
        b.Entity<Delivery>().HasIndex(d => new { d.Status, d.NextAttemptAt });
        b.Entity<Delivery>().HasMany(d => d.Log).WithOne().HasForeignKey(a => a.DeliveryId);
    }
}
