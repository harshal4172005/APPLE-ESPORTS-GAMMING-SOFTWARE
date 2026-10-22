using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Api.Hubs;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Api.Services;

/// <summary>
/// The branch's line to the shared fridge count at Head Office (see SharedStockGate).
///
/// Waits on Head Office's watch call, which answers the moment any count in the group moves, and
/// copies the new numbers into this branch's own menu - so a can sold at the other counter
/// disappears from this counter's screens and every gaming PC menu straight away. Before each
/// wait it sends anything sold while Head Office was unreachable (SharedStockPending), so the
/// counts it then receives already include those sales.
///
/// Also tells SharedStockGate which group this branch is in and who the offline seller is, and
/// keeps that on disk (SystemConfig) so a branch restarted with no internet still knows the rule.
/// </summary>
public class SharedStockWatcher : BackgroundService
{
    private const string MembershipKey = "shared_stock_membership";

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SharedStockWatcher> _logger;
    private readonly IHubContext<FoodOrderHub> _foodHub;
    private readonly IHubContext<PcOverlayHub> _overlayHub;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(35) };

    public SharedStockWatcher(IServiceProvider services, IConfiguration configuration, ILogger<SharedStockWatcher> logger,
        IHubContext<FoodOrderHub> foodHub, IHubContext<PcOverlayHub> overlayHub)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
        _foodHub = foodHub;
        _overlayHub = overlayHub;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var headOffice = _configuration["Sync:HeadOfficeUrl"]?.TrimEnd('/');
        if (_configuration.IsHeadOffice() || string.IsNullOrWhiteSpace(headOffice)) return;
        SharedStockGate.HeadOfficeUrl = headOffice;

        long since = 0;
        var fullRefreshAt = DateTimeOffset.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // Every few minutes ask for every count again, so an item that only reached this
                // branch's menu after its count last moved still picks up the right number.
                if (DateTimeOffset.UtcNow >= fullRefreshAt || SharedStockGate.RefreshRequested)
                {
                    SharedStockGate.RefreshRequested = false;
                    since = 0;
                    fullRefreshAt = DateTimeOffset.UtcNow.AddMinutes(5);
                }

                if (SharedStockGate.BranchId == Guid.Empty && !await LoadAsync(stop))
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stop);
                    continue;
                }

                if (!await FlushPendingAsync(stop))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stop);
                    continue;
                }

                since = await WatchOnceAsync(headOffice, since, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                SharedStockGate.Online = false;
                _logger.LogDebug("Shared fridge: Head Office not reachable ({Reason}).", ex.GetBaseException().Message);
                await Task.Delay(TimeSpan.FromSeconds(3), stop);
            }
        }
    }

    /// <summary>This branch's id, and the group rule it last knew.</summary>
    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var branchId = await db.Branches.AsNoTracking().OrderBy(b => b.Id).Select(b => b.Id).FirstOrDefaultAsync(ct);
        if (branchId == Guid.Empty) return false;   // not adopted yet
        SharedStockGate.BranchId = branchId;

        var saved = await db.Set<SystemConfig>().AsNoTracking()
            .Where(c => c.ConfigKey == MembershipKey).Select(c => c.ConfigValue).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(saved) && saved != "null")
            SharedStockGate.Membership = JsonSerializer.Deserialize<SharedStockMembership>(saved);
        return true;
    }

    /// <summary>Sends what was sold offline, oldest first. False = Head Office still unreachable.</summary>
    private async Task<bool> FlushPendingAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pending = await db.SharedStockPending.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(ct);
        foreach (var p in pending)
        {
            // The pending row's own id is the request id, so a resend after a lost answer is
            // recognised at Head Office and never applied twice.
            var answer = await SharedStockGate.SendAsync(p.Id, p.InventoryItemId, p.Op, p.Quantity, 0, replay: true, ct);
            if (answer.Outcome == SharedStockGate.Outcome.Unreachable) return false;
            await db.SharedStockPending.Where(x => x.Id == p.Id).ExecuteDeleteAsync(ct);
        }
        return true;
    }

    private async Task<long> WatchOnceAsync(string headOffice, long since, CancellationToken ct)
    {
        var reply = await Http.GetFromJsonAsync<Envelope>(
            $"{headOffice}/api/shared-stock/watch?branchId={SharedStockGate.BranchId}&since={since}", ct);
        SharedStockGate.Online = true;

        var data = reply?.Data;
        var membership = data?.GroupId is { } groupId
            ? new SharedStockMembership(groupId, data.SellerBranchId, data.SellerName)
            : null;
        await RememberAsync(membership, ct);

        if (membership is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);   // not sharing - just check now and then
            return 0;
        }

        if (data!.Counts is { Count: > 0 } counts)
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var skipped = false;
            foreach (var c in counts)
            {
                // This branch is saving this item right now and will write Head Office's answer
                // itself - writing it here first is what made that save fail (SharedStockGate.InFlight).
                if (SharedStockGate.IsInFlight(c.InventoryItemId)) { skipped = true; continue; }

                var item = await db.InventoryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == c.InventoryItemId, ct);
                if (item is null || item.CurrentStock == c.Count) continue;
                SharedStockGate.SetAvailability(item, c.Count);

                // Written straight to the table, not through SaveChanges: this is Head Office's
                // number arriving, not a change this branch made, so the gate must not send it back.
                await db.InventoryItems.Where(i => i.Id == item.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.CurrentStock, c.Count)
                    .SetProperty(i => i.Status, item.Status)
                    .SetProperty(i => i.UpdatedAt, DateTimeOffset.UtcNow), ct);
            }

            await _foodHub.Clients.Group($"branch:{SharedStockGate.BranchId}").SendAsync("StockChanged", ct);
            await _overlayHub.Clients.All.SendAsync("StockChanged", ct);

            // Something was left for a save in progress: look again in a moment, from the start,
            // so a change from the other counter that arrived in the same batch is not lost.
            if (skipped)
            {
                await Task.Delay(300, ct);
                return 0;
            }
        }

        return data.Version;
    }

    private async Task RememberAsync(SharedStockMembership? membership, CancellationToken ct)
    {
        if (Equals(SharedStockGate.Membership, membership)) return;
        SharedStockGate.Membership = membership;

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var value = JsonSerializer.Serialize(membership);
        var row = await db.Set<SystemConfig>().FirstOrDefaultAsync(c => c.ConfigKey == MembershipKey, ct);
        if (row is null)
            db.Add(new SystemConfig { Id = Guid.NewGuid(), ConfigKey = MembershipKey, ConfigValue = value, Description = "Shared fridge group (SharedStockWatcher)", UpdatedAt = DateTimeOffset.UtcNow });
        else
        {
            row.ConfigValue = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        if (membership is null) await db.SharedStockPending.ExecuteDeleteAsync(ct);   // no fridge to send them to
        _logger.LogInformation("Shared fridge: {State}.", membership is null ? "this branch shares no fridge"
            : $"group {membership.GroupId}, offline seller {membership.SellerName ?? "none"}");
    }

    private sealed class Envelope { public WatchReply? Data { get; set; } }

    private sealed class WatchReply
    {
        public Guid? GroupId { get; set; }
        public Guid? SellerBranchId { get; set; }
        public string? SellerName { get; set; }
        public long Version { get; set; }
        public List<CountDto>? Counts { get; set; }
    }

    private sealed class CountDto
    {
        public Guid InventoryItemId { get; set; }
        public int Count { get; set; }
    }
}
