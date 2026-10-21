using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Infrastructure.Configuration;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Api.Controllers;

/// <summary>
/// Head Office's side of the shared fridge - see SharedStockGate for the branch's side.
///
/// apply: one change to one item's count, taken strictly one at a time per item (a single
///        conditional UPDATE, so of two "take the last can" requests exactly one succeeds).
/// watch: a branch waits here for any change in its group and gets it the moment it happens,
///        which is what keeps both counters' numbers instant without polling.
///
/// Anonymous like every other branch-to-Head-Office call (heartbeat, sync); a request only
/// counts for a known branch, and only against its own group's fridge.
/// </summary>
[ApiController]
[Route("api/shared-stock")]
[AllowAnonymous]
public class SharedStockController : ControllerBase
{
    /// <summary>A branch that has not waited on watch for this long is treated as offline.</summary>
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WatchFor = TimeSpan.FromSeconds(25);

    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastWatch = new();
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> Signals = new();

    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;

    public SharedStockController(AppDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public sealed class ApplyRequest
    {
        public Guid BranchId { get; set; }
        public Guid RequestId { get; set; }
        public Guid InventoryItemId { get; set; }
        public string Op { get; set; } = "";
        public int Quantity { get; set; }
        public int SeedCount { get; set; }
        public bool Replay { get; set; }
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplyRequest r, CancellationToken ct)
    {
        if (!_configuration.IsHeadOffice()) return NotFound();
        if (r.Op is not ("take" or "add" or "set") || r.Quantity < 0)
            return BadRequest(ApiResponse<object>.Fail("Unknown stock change."));

        var branch = await _db.Branches.AsNoTracking()
            .Where(b => b.Id == r.BranchId)
            .Select(b => new { b.FoodGroupId, SellerId = b.FoodGroup!.OfflineSellerBranchId, SellerName = b.FoodGroup.Branches.Where(s => s.Id == b.FoodGroup.OfflineSellerBranchId).Select(s => s.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (branch?.FoodGroupId is not { } groupId)
            return BadRequest(ApiResponse<object>.Fail("This branch does not share a fridge.", "NOT_SHARED"));

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Same request again (a retry after a lost answer): return what it got the first time.
        var fresh = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO shared_stock_requests (""Id"", ""FoodGroupId"", ""InventoryItemId"", ""BranchId"", ""Op"", ""Quantity"", ""CreatedAt"")
            VALUES ({r.RequestId}, {groupId}, {r.InventoryItemId}, {r.BranchId}, {r.Op}, {r.Quantity}, now())
            ON CONFLICT (""Id"") DO NOTHING", ct);
        if (fresh == 0)
        {
            var earlier = await _db.SharedStockRequests.AsNoTracking().FirstAsync(q => q.Id == r.RequestId, ct);
            return Reply(earlier.RefusedReason is null, earlier.ResultCount ?? 0, earlier.RefusedReason);
        }

        // First time Head Office hears of this item: start from the branch's own count.
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO shared_stock_counts (""FoodGroupId"", ""InventoryItemId"", ""Count"", ""Version"", ""UpdatedAt"")
            VALUES ({groupId}, {r.InventoryItemId}, {r.SeedCount}, nextval('shared_stock_version_seq'), now())
            ON CONFLICT DO NOTHING", ct);

        string? refused = null;
        int? count = null;

        // While the offline seller is cut off it is the only one selling this fridge; a sale or
        // count anywhere else would be blind to it.
        var sellerOffline = branch.SellerId is { } sellerId && sellerId != r.BranchId
            && LastWatch.TryGetValue(sellerId, out var seen) && DateTimeOffset.UtcNow - seen > OfflineAfter;
        if (!r.Replay && r.Op != "add" && sellerOffline)
            refused = $"{branch.SellerName ?? "The main counter"} has no internet, so the shared fridge is sold only there until it is back.";
        else
        {
            var rows = r.Op switch
            {
                "take" => await _db.Database.SqlQuery<int>($@"
                    UPDATE shared_stock_counts SET ""Count"" = ""Count"" - {r.Quantity}, ""Version"" = nextval('shared_stock_version_seq'), ""UpdatedAt"" = now()
                    WHERE ""FoodGroupId"" = {groupId} AND ""InventoryItemId"" = {r.InventoryItemId} AND (""Count"" >= {r.Quantity} OR {r.Replay})
                    RETURNING ""Count"" AS ""Value""").ToListAsync(ct),
                "add" => await _db.Database.SqlQuery<int>($@"
                    UPDATE shared_stock_counts SET ""Count"" = ""Count"" + {r.Quantity}, ""Version"" = nextval('shared_stock_version_seq'), ""UpdatedAt"" = now()
                    WHERE ""FoodGroupId"" = {groupId} AND ""InventoryItemId"" = {r.InventoryItemId}
                    RETURNING ""Count"" AS ""Value""").ToListAsync(ct),
                _ => await _db.Database.SqlQuery<int>($@"
                    UPDATE shared_stock_counts SET ""Count"" = {r.Quantity}, ""Version"" = nextval('shared_stock_version_seq'), ""UpdatedAt"" = now()
                    WHERE ""FoodGroupId"" = {groupId} AND ""InventoryItemId"" = {r.InventoryItemId}
                    RETURNING ""Count"" AS ""Value""").ToListAsync(ct),
            };

            if (rows.Count == 1) count = rows[0];
            else
            {
                var left = await _db.SharedStockCounts.AsNoTracking()
                    .Where(c => c.FoodGroupId == groupId && c.InventoryItemId == r.InventoryItemId)
                    .Select(c => c.Count).FirstAsync(ct);
                var name = await _db.InventoryItems.AsNoTracking()
                    .Where(i => i.Id == r.InventoryItemId).Select(i => i.ItemName).FirstOrDefaultAsync(ct) ?? "This item";
                count = left;
                refused = left <= 0
                    ? $"{name} is sold out - the last one was just sold at the other counter."
                    : $"Only {left} {name} left in the fridge.";
            }
        }

        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE shared_stock_requests SET ""ResultCount"" = {count}, ""RefusedReason"" = {refused} WHERE ""Id"" = {r.RequestId}", ct);
        await tx.CommitAsync(ct);

        if (refused is null) Wake(groupId);
        return Reply(refused is null, count ?? 0, refused);
    }

    /// <summary>
    /// Every count in the branch's group changed since <paramref name="since"/>; waits up to
    /// 25 s for one when nothing has. Also how Head Office knows a branch is online.
    /// </summary>
    [HttpGet("watch")]
    public async Task<IActionResult> Watch([FromQuery] Guid branchId, [FromQuery] long since, CancellationToken ct)
    {
        if (!_configuration.IsHeadOffice()) return NotFound();
        LastWatch[branchId] = DateTimeOffset.UtcNow;

        var group = await _db.Branches.AsNoTracking()
            .Where(b => b.Id == branchId && b.FoodGroupId != null)
            .Select(b => new
            {
                GroupId = b.FoodGroupId!.Value,
                SellerBranchId = b.FoodGroup!.OfflineSellerBranchId,
                SellerName = b.FoodGroup.Branches.Where(s => s.Id == b.FoodGroup.OfflineSellerBranchId).Select(s => s.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (group is null) return Ok(ApiResponse<object>.Ok(new { groupId = (Guid?)null }));

        var signal = Signals.GetOrAdd(group.GroupId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var changed = await ChangedSince(group.GroupId, since, ct);
        if (changed.Count == 0)
        {
            try { await signal.Task.WaitAsync(WatchFor, ct); } catch (TimeoutException) { }
            changed = await ChangedSince(group.GroupId, since, ct);
        }
        LastWatch[branchId] = DateTimeOffset.UtcNow;

        return Ok(ApiResponse<object>.Ok(new
        {
            groupId = group.GroupId,
            group.SellerBranchId,
            group.SellerName,
            version = changed.Count > 0 ? changed.Max(c => c.Version) : since,
            counts = changed.Select(c => new { c.InventoryItemId, c.Count }),
        }));
    }

    /// <summary>At a branch: whether it shares a fridge and can sell from it right now (the banner).</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var m = SharedStockGate.Membership;
        return Ok(ApiResponse<object>.Ok(new
        {
            shared = m is not null,
            online = SharedStockGate.Online,
            isSeller = SharedStockGate.IsSeller,
            sellerName = m?.SellerName,
            canSell = SharedStockGate.CanSellShared,
            message = m is not null && !SharedStockGate.Online
                ? (m.SellerBranchId is null
                    ? "No internet - the fridge count is not live. Sales here are sent to Head Office when the line is back; check the shelf."
                    : SharedStockGate.IsSeller
                        ? "No internet - selling the shared fridge from this counter only. The other branch's counter is paused."
                        : SharedStockGate.OfflineMessage(m))
                : null,
        }));
    }

    private Task<List<Domain.Entities.SharedStockCount>> ChangedSince(Guid groupId, long since, CancellationToken ct) =>
        _db.SharedStockCounts.AsNoTracking()
            .Where(c => c.FoodGroupId == groupId && c.Version > since)
            .ToListAsync(ct);

    private static void Wake(Guid groupId)
    {
        if (Signals.TryRemove(groupId, out var signal)) signal.TrySetResult();
    }

    private OkObjectResult Reply(bool applied, int count, string? refusedReason) =>
        Ok(ApiResponse<object>.Ok(new { applied, count, refusedReason }));
}
