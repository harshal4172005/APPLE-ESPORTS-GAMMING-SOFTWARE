using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;

namespace AppleEsportsErp.Infrastructure.Data;

/// <summary>Which shared fridge this branch sells from, as Head Office last said.</summary>
public sealed record SharedStockMembership(Guid GroupId, Guid? SellerBranchId, string? SellerName);

/// <summary>
/// One fridge, one count. Branches in a food group sell from one physical shelf, so the count
/// lives at Head Office (SharedStockController) and every change to a shared item's stock -
/// an order, a cancel, a delivery, a count, from any screen - is asked of Head Office before
/// it is saved here. Head Office takes them one at a time, so two counters can never both get
/// the last can; its answer becomes this branch's number.
///
/// Hooked into AppDbContext.SaveChangesAsync, so there is no screen or service that can move a
/// shared item's stock without coming through here.
///
/// Head Office unreachable: only the group's offline seller keeps selling, from its own count,
/// queuing each change (SharedStockPending) for SharedStockWatcher to send when the line is
/// back. Every other branch refuses - two counters selling the same fridge blind is the exact
/// thing this exists to prevent. Additions (a delivery, a cancelled order) queue anywhere.
///
/// Before this, each branch kept its own count and the changes were relayed between them,
/// which let a stock count add on top of the other branch's count (41 became 82) and, with
/// every relayed change echoed back, ran a loop between the two Adajan branches for a day.
/// </summary>
public static class SharedStockGate
{
    /// <summary>Null = this branch shares no fridge and nothing here applies. Set by SharedStockWatcher.</summary>
    public static volatile SharedStockMembership? Membership;
    public static string? HeadOfficeUrl;
    public static Guid BranchId;
    /// <summary>This branch's own name, so the other counters can be told who counted the fridge.</summary>
    public static volatile string? BranchName;

    /// <summary>
    /// Why a shift count did not change the shared fridge ("already counted by X", or no line to
    /// Head Office). Set during the save, read by the stock-count screen straight after, so the
    /// operator is told instead of quietly keeping a number that was never used.
    /// </summary>
    public static readonly System.Threading.AsyncLocal<List<string>?> CountNotes = new();

    /// <summary>
    /// Who last counted each shared item and when, as Head Office last said. Read by the stock
    /// count screen so a counter is told "already counted by X" before it asks for a number.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTimeOffset At, string? By)> LastCount = new();

    /// <summary>Whether the last call to Head Office got through. Drives the "not live" banner.</summary>
    public static volatile bool Online = true;

    /// <summary>
    /// This branch may sell the shared fridge with no line to Head Office: it is the group's
    /// chosen offline seller - or no seller has been chosen, in which case every branch keeps
    /// selling from its own count rather than the whole group stopping (Settings → Food sharing).
    /// </summary>
    public static bool IsSeller => Membership is { } m && (m.SellerBranchId == BranchId || m.SellerBranchId is null);

    /// <summary>False while offline at a branch that is not the group's offline seller.</summary>
    public static bool CanSellShared => Membership is null || Online || IsSeller;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public enum Outcome { Applied, Refused, Unreachable }
    public sealed record Answer(Outcome Outcome, int Count = 0, string? Reason = null);
    internal sealed record AppliedOp(Guid ItemId, string Op, int Quantity, int Before);

    // Items this branch is in the middle of saving. Head Office pushes every change straight
    // back to every branch in the group - including the one that made it - and the watcher
    // writing that echo into the row a few milliseconds before this branch's own save made the
    // save fail (InventoryItem is xmin-protected): 22 Oct, every stock count at Citylight-144Hz
    // refused, 7 of 10 orders in the reproduction. The watcher leaves held items alone
    // (SharedStockWatcher); the save itself writes Head Office's answer for them anyway.
    private static readonly ConcurrentDictionary<Guid, int> InFlight = new();
    public static bool IsInFlight(Guid itemId) => InFlight.ContainsKey(itemId);

    /// <summary>Set when a save had to overwrite a newer mirror value; the watcher re-reads everything.</summary>
    public static volatile bool RefreshRequested;

    private static void Hold(Guid itemId) => InFlight.AddOrUpdate(itemId, 1, (_, n) => n + 1);

    internal static void Release(List<Guid> held)
    {
        foreach (var id in held)
        {
            while (InFlight.TryGetValue(id, out var n))
            {
                var done = n <= 1
                    ? InFlight.TryRemove(new KeyValuePair<Guid, int>(id, n))
                    : InFlight.TryUpdate(id, n - 1, n);
                if (done) break;
            }
        }
        held.Clear();
    }

    /// <summary>Runs inside SaveChangesAsync, before anything is written. Every item it asks Head
    /// Office about is added to <paramref name="held"/>, which the caller releases after its save.</summary>
    internal static async Task<List<AppliedOp>> ApplyAsync(AppDbContext db, List<Guid> held, CancellationToken ct)
    {
        var applied = new List<AppliedOp>();
        var m = Membership;
        if (m is null || HeadOfficeUrl is null) return applied;

        // A count (shift handover, reconcile, stock edit) logs a "discrepancy": it SETS the
        // number. Anything else moved it by an amount.
        var counted = db.ChangeTracker.Entries<InventoryLog>()
            .Where(l => l.State == EntityState.Added && l.Entity.Action == "discrepancy")
            .Select(l => l.Entity.InventoryId)
            .ToHashSet();

        var changes = db.ChangeTracker.Entries<InventoryItem>()
            .Where(e => e.State == EntityState.Modified
                && e.Property(i => i.CurrentStock).IsModified
                && e.Property(i => i.CurrentStock).CurrentValue != e.Property(i => i.CurrentStock).OriginalValue)
            .ToList();

        foreach (var entry in changes)
        {
            var stock = entry.Property(i => i.CurrentStock);
            var before = stock.OriginalValue;
            var after = stock.CurrentValue;
            var (op, qty) = counted.Contains(entry.Entity.Id) ? ("set", after)
                : after < before ? ("take", before - after)
                : ("add", after - before);

            Hold(entry.Entity.Id);
            held.Add(entry.Entity.Id);
            var answer = await SendAsync(Guid.NewGuid(), entry.Entity.Id, op, qty, before, replay: false, ct,
                countedByName: op == "set" ? BranchName : null);

            if (answer.Outcome == Outcome.Applied)
            {
                applied.Add(new AppliedOp(entry.Entity.Id, op, qty, before));
                stock.CurrentValue = answer.Count;
                SetAvailability(entry.Entity, answer.Count);
                continue;
            }

            // A count that cannot count right now (this branch offline and not the seller, or the
            // seller offline) is left on this screen only and never sent: refusing it would
            // block the whole shift handover it is part of, cash count included. Head Office's
            // number replaces it here as soon as the line is back (SharedStockWatcher).
            if (op == "set" && (answer.Outcome == Outcome.Refused || !IsSeller))
            {
                // Not applied, so this branch must not keep its own number either - the fridge is
                // whatever Head Office says. Put the item back and tell the operator why.
                stock.CurrentValue = answer.Outcome == Outcome.Refused ? answer.Count : before;
                SetAvailability(entry.Entity, stock.CurrentValue);
                CountNotes.Value?.Add(answer.Outcome == Outcome.Refused
                    ? $"{entry.Entity.ItemName}: {answer.Reason}"
                    : $"{entry.Entity.ItemName}: no line to Head Office, so the shared fridge was not changed.");
                continue;
            }

            if (answer.Outcome == Outcome.Refused)
            {
                await UndoAsync(applied);
                throw new AppException(answer.Reason ?? $"{entry.Entity.ItemName} is sold out.",
                    HttpStatusCode.Conflict, "SHARED_STOCK_REFUSED");
            }

            // Head Office unreachable.
            if (op != "add" && !IsSeller)
            {
                await UndoAsync(applied);
                throw new AppException(OfflineMessage(m), HttpStatusCode.ServiceUnavailable, "SHARED_STOCK_OFFLINE");
            }

            if (op == "take" && before < qty)
            {
                await UndoAsync(applied);
                throw new AppException($"Insufficient stock for {entry.Entity.ItemName}. Available: {before}");
            }

            SetAvailability(entry.Entity, after);
            db.SharedStockPending.Add(new SharedStockPending
            {
                Id = Guid.NewGuid(),
                InventoryItemId = entry.Entity.Id,
                Op = op,
                Quantity = qty,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        return applied;
    }

    /// <summary>
    /// Head Office's live count of every shared item, asked for right now.
    ///
    /// The count screen needs this before it shows "system says": the root of the Citylight mess
    /// on 31 Oct was 144Hz comparing the shelf against its own copy, which still said 0 while the
    /// fridge held 14 - so a correct count of 14 was reported as "14 extra". The watcher keeps
    /// this up to date in the background; this asks once more at the only moment it has to be right.
    /// Empty when there is no line to Head Office: the screen then says so.
    /// </summary>
    public static async Task<Dictionary<Guid, int>> LiveCountsAsync(CancellationToken ct)
    {
        var live = new Dictionary<Guid, int>();
        if (Membership is null || HeadOfficeUrl is null) return live;
        try
        {
            var reply = await Http.GetFromJsonAsync<WatchEnvelope>(
                $"{HeadOfficeUrl}/api/shared-stock/watch?branchId={BranchId}&since=0", ct);
            foreach (var c in reply?.Data?.Counts ?? new List<WatchCount>())
            {
                live[c.InventoryItemId] = c.Count;
                if (c.CountedAt is { } at) LastCount[c.InventoryItemId] = (at, c.CountedByName);
            }
            Online = true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Online = false;
        }
        return live;
    }

    private sealed class WatchEnvelope { public WatchBody? Data { get; set; } }
    private sealed class WatchBody { public List<WatchCount>? Counts { get; set; } }
    private sealed class WatchCount
    {
        public Guid InventoryItemId { get; set; }
        public int Count { get; set; }
        public DateTimeOffset? CountedAt { get; set; }
        public string? CountedByName { get; set; }
    }

    public static string OfflineMessage(SharedStockMembership m) =>
        $"No internet - the shared fridge is sold only at {m.SellerName ?? "the main counter"} until it is back.";

    /// <summary>
    /// Puts back what Head Office applied for a save that then did not happen here.
    /// ponytail: best effort - if Head Office has just become unreachable the undo is lost and
    /// the next shift count corrects it; queue it if that ever shows up in practice.
    /// </summary>
    internal static async Task UndoAsync(List<AppliedOp> applied)
    {
        foreach (var a in applied)
        {
            var (op, qty) = a.Op switch
            {
                "take" => ("add", a.Quantity),
                "add" => ("take", a.Quantity),
                _ => ("set", a.Before),
            };
            await SendAsync(Guid.NewGuid(), a.ItemId, op, qty, a.Before, replay: true, CancellationToken.None);
        }
        applied.Clear();
    }

    /// <summary>
    /// One request to Head Office. <paramref name="replay"/> = this already happened (a sale
    /// made offline, or an undo): apply it even below zero, never refuse it.
    /// </summary>
    public static async Task<Answer> SendAsync(
        Guid requestId, Guid itemId, string op, int qty, int seedCount, bool replay, CancellationToken ct,
        string? countedByName = null)
    {
        try
        {
            var response = await Http.PostAsJsonAsync($"{HeadOfficeUrl}/api/shared-stock/apply", new
            {
                branchId = BranchId,
                requestId,
                inventoryItemId = itemId,
                op,
                quantity = qty,
                seedCount,
                replay,
                countedByName,
            }, ct);

            if (!response.IsSuccessStatusCode) { Online = false; return new Answer(Outcome.Unreachable); }

            var body = await response.Content.ReadFromJsonAsync<Envelope>(cancellationToken: ct);
            Online = true;
            if (body?.Data is not { } d) return new Answer(Outcome.Unreachable);

            // Another counter counted the same fridge minutes ago and made it something else.
            // Both numbers are put in front of this operator rather than one silently winning.
            if (d.PreviousCountBy is { } who && d.PreviousCount is { } theirs && theirs != qty)
                CountNotes.Value?.Add(
                    $"{who} counted {theirs} at {AppleEsportsErp.Application.Services.IndiaTime.Format(d.PreviousCountAt!.Value)}; "
                    + $"you counted {qty}. The fridge is now {d.Count} - check the shelf if that looks wrong.");

            return d.Applied ? new Answer(Outcome.Applied, d.Count)
                : new Answer(Outcome.Refused, d.Count, d.RefusedReason);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Online = false;
            return new Answer(Outcome.Unreachable);
        }
    }

    public static void SetAvailability(InventoryItem item, int count)
    {
        if (count <= 0 && item.Status == FoodAvailability.Available) item.Status = FoodAvailability.OutOfStock;
        else if (count > 0 && item.Status == FoodAvailability.OutOfStock) item.Status = FoodAvailability.Available;
    }

    private sealed class Envelope { public ApplyReply? Data { get; set; } }

    public sealed class ApplyReply
    {
        public bool Applied { get; set; }
        public int Count { get; set; }
        public string? RefusedReason { get; set; }
        public string? PreviousCountBy { get; set; }
        public DateTimeOffset? PreviousCountAt { get; set; }
        public int? PreviousCount { get; set; }
    }
}
