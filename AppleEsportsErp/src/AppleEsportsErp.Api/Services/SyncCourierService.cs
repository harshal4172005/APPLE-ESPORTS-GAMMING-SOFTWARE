using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Services;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Api.Services;

public class SyncCourierService : BackgroundService
{
    private readonly ILogger<SyncCourierService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    /// <summary>
    /// How long a bill can sit at the branch before Head Office is told.
    ///
    /// Thirty seconds was chosen when nothing else was moving. Once the branch began reporting
    /// its live state every three seconds, the gap between the two became the problem: the
    /// heartbeat would show a PC busy while the session that made it busy had not arrived yet,
    /// so the owner saw a shop trading with no sessions behind it, then watched the sessions
    /// appear half a minute later. Both halves correct, the pair of them wrong together.
    ///
    /// Three seconds, to match. The query behind it is one indexed look for undelivered rows
    /// and finds nothing almost every time, and a branch with nothing to send posts nothing at
    /// all - so a quiet shop costs a local database query every three seconds and no traffic.
    /// </summary>
    private readonly int _pollIntervalSeconds = 3;

    private readonly int _maxAttempts = 5;
    private readonly int _batchSize = 100;

    // When each branch's link to Head Office was first seen to be down, so the outage can be
    // reported with a start time rather than just "it failed again". Cleared on the first
    // successful delivery, which is the moment the link came back.
    private readonly Dictionary<Guid, DateTimeOffset> _offlineSince = new();

    public SyncCourierService(
        ILogger<SyncCourierService> logger,
        IConfiguration configuration,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _pollIntervalSeconds = configuration.GetValue("Sync:PollIntervalSeconds", 3);
        _maxAttempts = configuration.GetValue("Sync:MaxRetryAttempts", 5);
        _batchSize = configuration.GetValue("Sync:BatchSize", 100);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Head Office has nowhere to send to, and the same build runs in both roles. Without
        // this it polled every 30 seconds forever and logged a warning per branch each time,
        // which is noise on the one instance whose logs matter most — and it buries the very
        // warning that means something on a branch that genuinely cannot reach Head Office.
        if (_configuration.IsHeadOffice())
        {
            _logger.LogInformation(
                "No Sync:HeadOfficeUrl configured, so this instance is Head Office. " +
                "The sync courier will not run; it is branches that report upward, not Head Office.");
            return;
        }

        _logger.LogInformation("Sync Courier service starting. Poll interval: {IntervalSeconds}s, Max attempts: {MaxAttempts}, Batch size: {BatchSize}",
            _pollIntervalSeconds, _maxAttempts, _batchSize);

        await ReviveGatewayCasualtiesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxEntriesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in sync courier service");
            }

            await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
        }
    }

    private async Task ProcessOutboxEntriesAsync(CancellationToken cancellationToken)
    {
        using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Fetch unsent entries (max _batchSize at a time)
            var unsentEntries = await context.SyncOutboxEntries
                .Where(e => e.SyncedAt == null && e.AttemptCount < _maxAttempts)
                .OrderBy(e => e.CreatedAt)
                .Take(_batchSize)
                .ToListAsync(cancellationToken);

            if (!unsentEntries.Any())
                return;

            _logger.LogDebug("Processing {Count} unsent sync outbox entries", unsentEntries.Count);

            // Group by branch (send each branch's entries to Head Office separately)
            var groupedByBranch = unsentEntries.GroupBy(e => e.BranchId);

            foreach (var branchGroup in groupedByBranch)
            {
                await SendBranchSyncBatchAsync(context, branchGroup.ToList(), cancellationToken);
            }
        }
    }

    private async Task SendBranchSyncBatchAsync(AppDbContext context, List<SyncOutboxEntry> entries, CancellationToken cancellationToken)
    {
        var branchId = entries.First().BranchId;
        var headOfficeUrl = _configuration["Sync:HeadOfficeUrl"];

        if (string.IsNullOrWhiteSpace(headOfficeUrl))
        {
            _logger.LogWarning("Sync:HeadOfficeUrl not configured, skipping sync for branch {BranchId}", branchId);
            return;
        }

        try
        {
            using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                var syncBatch = new
                {
                    branchId,
                    entries = entries.Select(e => new
                    {
                        id = e.Id,
                        aggregateType = e.AggregateType,
                        aggregateId = e.AggregateId,
                        eventType = e.EventType,
                        eventData = JsonSerializer.Deserialize<object>(e.EventData),
                        createdAt = e.CreatedAt
                    }).ToList()
                };

                var content = new StringContent(
                    JsonSerializer.Serialize(syncBatch),
                    Encoding.UTF8,
                    "application/json");

                var response = await httpClient.PostAsync(
                    $"{headOfficeUrl.TrimEnd('/')}/api/sync/receive",
                    content,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    // Mark all entries as synced
                    foreach (var entry in entries)
                    {
                        entry.SyncedAt = DateTime.UtcNow;
                        entry.AttemptCount++;
                    }

                    // The link is back. Close off the outage before saving, so the record and
                    // the delivery land in the same write.
                    await CloseInternetOutageAsync(context, branchId, cancellationToken);

                    await context.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Successfully synced {Count} entries from branch {BranchId}", entries.Count, branchId);
                }
                else
                {
                    var errorMsg = await response.Content.ReadAsStringAsync(cancellationToken);
                    // 502/503/504 are nginx answering while Head Office's API restarts (every
                    // deploy) - the same "can't reach it" as the internet being down, not a
                    // verdict on the record. Counted as refusals, five of them in ~15 seconds
                    // abandoned whole batches for good.
                    var headOfficeDown = IsGatewayError(response.StatusCode);
                    if (headOfficeDown) MarkInternetDown(branchId);
                    HandleSyncFailure(context, entries, $"HTTP {response.StatusCode}: {errorMsg}", transportFailure: headOfficeDown);
                    _logger.LogWarning("Failed to sync {Count} entries from branch {BranchId}: {Status}", entries.Count, branchId, response.StatusCode);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            // Cannot reach Head Office at all — this is the internet being down, as opposed
            // to Head Office answering with an error.
            MarkInternetDown(branchId);
            HandleSyncFailure(context, entries, $"Network error: {ex.Message}", transportFailure: true);
            _logger.LogWarning(ex, "Network error syncing branch {BranchId}", branchId);
        }
        catch (TaskCanceledException ex)
        {
            MarkInternetDown(branchId);
            HandleSyncFailure(context, entries, $"Request timeout: {ex.Message}", transportFailure: true);
            _logger.LogWarning(ex, "Timeout syncing branch {BranchId}", branchId);
        }
        catch (Exception ex)
        {
            HandleSyncFailure(context, entries, $"Unexpected error: {ex.Message}", transportFailure: false);
            _logger.LogError(ex, "Unexpected error syncing branch {BranchId}", branchId);
        }
    }

    /// <summary>
    /// Notes the first moment this branch's link went down. Only the first failure sets the
    /// clock — a link that has been down for an hour should read as one hour-long outage, not
    /// a hundred and twenty separate thirty-second ones.
    /// </summary>
    private void MarkInternetDown(Guid branchId)
    {
        if (!_offlineSince.ContainsKey(branchId))
        {
            _offlineSince[branchId] = DateTimeOffset.UtcNow;
            _logger.LogWarning("Branch {BranchId} has lost its link to Head Office.", branchId);
        }
    }

    /// <summary>
    /// Records a completed internet outage once delivery succeeds again.
    ///
    /// Written only on recovery, because until then we do not know when it ended. Very short
    /// blips are discarded: a single missed poll is normal on any connection, and filling the
    /// owner's report with thirty-second entries would bury the outages that actually mattered.
    ///
    /// Deliberately separate from a power cut. Play was never interrupted here — the branch
    /// ran perfectly on its own and no customer noticed — so nothing is credited back. It is
    /// recorded so the owner knows why a branch's figures arrived late.
    /// </summary>
    private async Task CloseInternetOutageAsync(AppDbContext context, Guid branchId, CancellationToken cancellationToken)
    {
        if (!_offlineSince.TryGetValue(branchId, out var since)) return;
        _offlineSince.Remove(branchId);

        var now = DateTimeOffset.UtcNow;
        var seconds = (int)(now - since).TotalSeconds;

        const int worthReportingSeconds = 120;
        if (seconds < worthReportingSeconds)
        {
            _logger.LogInformation("Branch {BranchId} link restored after {Seconds}s — too brief to report.", branchId, seconds);
            return;
        }

        context.DowntimeEvents.Add(new DowntimeEvent
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            Kind = DowntimeKind.InternetOffline,
            StartedAt = since,
            EndedAt = now,
            DurationSeconds = seconds,
            SessionsAffected = 0,   // nobody's game was interrupted
            BusinessDay = IndiaTime.BusinessDayOf(since),
            Notes = "Branch could not reach Head Office. Play continued normally; data was queued and has now been delivered.",
            CreatedAt = now,
        });

        _logger.LogInformation(
            "Branch {BranchId} link restored after {Minutes:N0} minutes offline ({From} to {To} IST) — recorded for the day's report.",
            branchId, seconds / 60.0, IndiaTime.Format(since), IndiaTime.Format(now));

        // Sent on recovery, never during: there was no line to send it on. Said plainly,
        // because "the internet was down" reads alarmingly and nothing was actually lost -
        // the shop traded normally throughout and the figures have now arrived.
        var branchName = await context.Branches.Where(b => b.Id == branchId)
            .Select(b => b.Name).FirstOrDefaultAsync(cancellationToken) ?? "Unknown branch";

        using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IAdminNotifier>();
            var howLong = AdminEmailTemplate.Describe(TimeSpan.FromSeconds(seconds));

            await notifier.NotifyAsync(
                $"{branchName} had no internet for {howLong} - nothing was lost",
                AdminEmailTemplate.Compose(
                    $"{branchName} lost its internet",
                    AdminEmailTemplate.Amber,
                    $"{branchName} could not reach the main server for {howLong}. The shop carried on working normally the whole time - customers played, bills were taken, and nobody would have noticed. The internet is back and everything from those {howLong} has now come through.",
                    new[]
                    {
                        ("Branch", branchName),
                        ("", ""),
                        ("Internet went at", IndiaTime.Format(since)),
                        ("Came back at", IndiaTime.Format(now)),
                        ("Gone for", howLong),
                        ("", ""),
                        ("Did the shop stop?", "No - everything kept working"),
                        ("Was anything lost?", "No - it has all arrived now"),
                    },
                    headline: $"No internet for {howLong}",
                    footnote: "You are told about this so you know why this branch's figures showed up late. Nothing needs doing."));
        }
    }

    private static bool IsGatewayError(System.Net.HttpStatusCode code) =>
        code is System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Gives back the retry budget to records abandoned only because Head Office was mid-restart
    /// (see IsGatewayError) before that was treated as an outage. Safe to resend: Head Office
    /// skips any entry id it has already applied.
    /// </summary>
    private async Task ReviveGatewayCasualtiesAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var revived = await db.SyncOutboxEntries
                .Where(e => e.SyncedAt == null && e.AttemptCount >= _maxAttempts
                    && (e.LastError!.StartsWith("HTTP BadGateway")
                        || e.LastError.StartsWith("HTTP ServiceUnavailable")
                        || e.LastError.StartsWith("HTTP GatewayTimeout")))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.AttemptCount, 0), ct);

            if (revived > 0)
                _logger.LogWarning("Re-queued {Count} sync record(s) abandoned during a Head Office restart.", revived);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not re-queue sync records abandoned during a Head Office restart.");
        }
    }

    /// <param name="transportFailure">
    /// True when Head Office could not be reached at all — the internet is down, the line is
    /// bad, the server is being restarted. False when Head Office answered and refused the
    /// record.
    ///
    /// The two must not be counted the same way, and counting them the same way lost a whole
    /// evening's trading on the first branch this was ever tested on. The courier retries every
    /// thirty seconds and gives up after five attempts, so a shop whose internet was out for
    /// **two and a half minutes** had every queued record marked as beyond hope. They are then
    /// excluded from the query that looks for work, so reconnecting does not help: the takings
    /// sit in the outbox for ever while the branch reports itself perfectly healthy.
    ///
    /// That defeats the entire point of a durable outbox, which exists precisely because the
    /// line goes down. A branch being offline is the normal case this was built for, not a
    /// fault in the record, so it no longer consumes the retry budget.
    ///
    /// A refusal from Head Office is different: the record itself is wrong, and retrying it a
    /// thousand times will not make it right. Those still give up after five, so one bad record
    /// cannot block the queue behind it for ever.
    /// </param>
    private void HandleSyncFailure(
        AppDbContext context, List<SyncOutboxEntry> entries, string errorMsg, bool transportFailure)
    {
        foreach (var entry in entries)
        {
            entry.LastError = errorMsg;

            if (transportFailure) continue;

            entry.AttemptCount++;

            if (entry.AttemptCount >= _maxAttempts)
            {
                _logger.LogError("Sync entry {EntryId} exceeded max attempts ({MaxAttempts}). Last error: {Error}",
                    entry.Id, _maxAttempts, errorMsg);
            }
        }

        context.SaveChanges();
    }
}
