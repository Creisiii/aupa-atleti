using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AupaAtleti.Api;

public class WebhookOptions
{
    public int MaxAttempts { get; set; } = 8;
    public int BaseDelaySeconds { get; set; } = 60;
    public int MaxDelaySeconds { get; set; } = 6 * 60 * 60;
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>Must exceed BatchSize x TimeoutSeconds, or a slow batch could be claimed twice.</summary>
    public int LeaseSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 10;
    public int PollSeconds { get; set; } = 5;
    /// <summary>A subscription is switched off after this many failed attempts in a row.</summary>
    public int DisableAfterFailures { get; set; } = 20;
    /// <summary>Only for local development and tests. Keep false anywhere reachable from the internet.</summary>
    public bool AllowPrivateNetworks { get; set; }
}

public static class Backoff
{
    /// <summary>Exponential backoff with ±20% jitter: 1x, 2x, 4x… of the base delay, capped.</summary>
    public static TimeSpan Delay(int attempt, WebhookOptions o, Random random)
    {
        var seconds = Math.Min(o.BaseDelaySeconds * Math.Pow(2, attempt - 1), o.MaxDelaySeconds);
        return TimeSpan.FromSeconds(seconds * (0.8 + random.NextDouble() * 0.4));
    }
}

public static class WebhookSignature
{
    public const string Header = "X-Aupa-Signature";

    /// <summary>
    /// HMAC-SHA256 over "{timestamp}.{body}". The timestamp is signed too, so a captured request
    /// cannot be replayed later with a fresh timestamp.
    /// </summary>
    public static string Sign(string secret, long unixSeconds, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{unixSeconds}.{body}"));
        return $"t={unixSeconds},v1={Convert.ToHexStringLower(mac)}";
    }

    /// <summary>What a receiver should do. Lives here so the tests exercise the documented check.</summary>
    public static bool Verify(string secret, string header, string body, DateTimeOffset now, TimeSpan tolerance)
    {
        var parts = header.Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        if (!parts.TryGetValue("t", out var t) || !long.TryParse(t, out var ts)) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration() > tolerance) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, ts, body)), Encoding.UTF8.GetBytes(header));
    }
}

/// <summary>
/// Subscribers choose the URL we call, so without this check they could point us at our own
/// network (localhost, cloud metadata, internal services).
/// </summary>
public static class SsrfGuard
{
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                 // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224);                                // multicast, reserved
        }
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast
            || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any));
    }

    public static string? ValidateUrl(string url, bool allowPrivate)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "URL is not valid";
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowPrivate && uri.Scheme == Uri.UriSchemeHttp)) return "URL must use https";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "URL must not contain credentials";
        if (!allowPrivate && IPAddress.TryParse(uri.Host, out var ip) && !IsPublic(ip)) return "URL points to a private address";
        return null;
    }

    /// <summary>
    /// Checks the address we actually connect to, not the one the name resolved to earlier:
    /// a hostname can resolve to a public IP when registered and a private one when we call it.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false, // a redirect could send us to a private address after the check
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault(a => allowPrivate || IsPublic(a))
                ?? throw new HttpRequestException($"Blocked: {ctx.DnsEndPoint.Host} resolves to a private address");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(target, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };
}

public static class EventPublisher
{
    /// <summary>
    /// Adds the event and one pending delivery per active subscription to the context WITHOUT saving.
    /// The caller saves them together with its own changes, so "the match was stored" and "the event
    /// exists" commit in one transaction (outbox pattern). Returns false if the event already exists.
    /// </summary>
    public static async Task<bool> PublishAsync(AppDb db, string type, string dedupKey, object payload, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Events.AnyAsync(e => e.DedupKey == dedupKey, ct)) return false;
        var ev = new OutboxEvent { Id = Guid.NewGuid(), Type = type, DedupKey = dedupKey, Payload = JsonSerializer.Serialize(payload), CreatedAt = now };
        db.Events.Add(ev);
        foreach (var subId in await db.Subscriptions.Where(s => s.Active).Select(s => s.Id).ToListAsync(ct))
            db.Deliveries.Add(new Delivery { Id = Guid.NewGuid(), EventId = ev.Id, SubscriptionId = subId, Status = DeliveryStatus.Pending, NextAttemptAt = now });
        return true;
    }
}

public class DeliveryService(AppDb db, IHttpClientFactory httpFactory, IOptions<WebhookOptions> options, TimeProvider time, ILogger<DeliveryService> log)
{
    private readonly WebhookOptions _o = options.Value;

    /// <summary>
    /// Claims due deliveries by taking a lease on them. FOR UPDATE SKIP LOCKED lets several workers run
    /// this at once and each get different rows; the lease (rather than a held transaction) means a
    /// worker that dies mid-send simply lets its rows become due again. That is at-least-once delivery.
    /// </summary>
    public async Task<List<Guid>> ClaimAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var leaseUntil = now.AddSeconds(_o.LeaseSeconds);
        var batch = _o.BatchSize;
        return await db.Database.SqlQuery<Guid>($"""
            UPDATE "Deliveries" SET "LockedUntil" = {leaseUntil}
            WHERE "Id" IN (
                SELECT "Id" FROM "Deliveries"
                WHERE "Status" = 0 AND "NextAttemptAt" <= {now} AND ("LockedUntil" IS NULL OR "LockedUntil" < {now})
                ORDER BY "NextAttemptAt" LIMIT {batch}
                FOR UPDATE SKIP LOCKED)
            RETURNING "Id" AS "Value"
            """).ToListAsync(ct);
    }

    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        var ids = await ClaimAsync(ct);
        foreach (var id in ids) await SendAsync(id, ct);
        return ids.Count;
    }

    public async Task SendAsync(Guid deliveryId, CancellationToken ct)
    {
        var d = await db.Deliveries.Include(x => x.Event).Include(x => x.Subscription).SingleAsync(x => x.Id == deliveryId, ct);
        var attempt = new DeliveryAttempt { DeliveryId = d.Id, At = time.GetUtcNow() };

        if (!d.Subscription.Active)
        {
            attempt.Error = "Subscription is disabled";
            d.Status = DeliveryStatus.Dead;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var req = BuildRequest(d, attempt.At);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
                using var res = await httpFactory.CreateClient("webhooks").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                attempt.StatusCode = (int)res.StatusCode;
                if (!res.IsSuccessStatusCode) attempt.Error = $"HTTP {(int)res.StatusCode}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { attempt.Error = "Timed out"; }
            catch (HttpRequestException e) { attempt.Error = e.Message; }
            attempt.DurationMs = (int)sw.ElapsedMilliseconds;

            d.Attempts++;
            if (attempt.Error is null)
            {
                d.Status = DeliveryStatus.Succeeded;
                d.Subscription.ConsecutiveFailures = 0;
            }
            else
            {
                if (++d.Subscription.ConsecutiveFailures >= _o.DisableAfterFailures) d.Subscription.Active = false;
                if (d.Attempts >= _o.MaxAttempts) d.Status = DeliveryStatus.Dead;
                else d.NextAttemptAt = attempt.At + Backoff.Delay(d.Attempts, _o, Random.Shared);
                log.LogWarning("Delivery {Id} attempt {Attempt} failed: {Error}", d.Id, d.Attempts, attempt.Error);
            }
        }

        if (d.Status != DeliveryStatus.Pending) d.CompletedAt = attempt.At;
        d.LockedUntil = null;
        db.DeliveryAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
    }

    private static HttpRequestMessage BuildRequest(Delivery d, DateTimeOffset now)
    {
        using var payload = JsonDocument.Parse(d.Event.Payload);
        // Discord only renders its own message shape, so that format sends the human-readable text.
        var body = d.Subscription.Format == SubscriptionFormat.Discord
            ? JsonSerializer.Serialize(new { content = payload.RootElement.TryGetProperty("text", out var t) ? t.GetString() : d.Event.Type })
            : JsonSerializer.Serialize(new { id = d.EventId, type = d.Event.Type, created_at = d.Event.CreatedAt, data = payload.RootElement });

        var req = new HttpRequestMessage(HttpMethod.Post, d.Subscription.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        // The event id is stable across retries: receivers use it to drop duplicates.
        req.Headers.Add("X-Aupa-Event-Id", d.EventId.ToString());
        req.Headers.Add("X-Aupa-Event-Type", d.Event.Type);
        req.Headers.Add(WebhookSignature.Header, WebhookSignature.Sign(d.Subscription.Secret, now.ToUnixTimeSeconds(), body));
        return req;
    }
}

public class DeliveryWorker(IServiceScopeFactory scopes, IOptions<WebhookOptions> options, ILogger<DeliveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sent = 0;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                sent = await scope.ServiceProvider.GetRequiredService<DeliveryService>().ProcessDueAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Delivery worker failed"); }
            // A full batch means there is probably more waiting: go again without sleeping.
            if (sent < options.Value.BatchSize) await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), ct);
        }
    }
}
