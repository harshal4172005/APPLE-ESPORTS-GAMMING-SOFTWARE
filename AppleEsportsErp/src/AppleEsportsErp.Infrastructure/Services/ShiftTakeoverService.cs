using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AppleEsportsErp.Application.Constants;
using AppleEsportsErp.Application.DTOs.Shift;
using AppleEsportsErp.Application.Exceptions;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Infrastructure.Data;

namespace AppleEsportsErp.Infrastructure.Services;

/// <summary>
/// One operator taking over a shift that nobody closed.
///
/// Login only ever looked for an active shift belonging to the <i>same</i> operator, so somebody
/// else's abandoned shift was invisible: the next person logged in, a second shift opened
/// alongside the first, and the abandoned one dangled with uncounted takings against it.
///
/// The owner's answer: "B will close A's shift and count all the things and then B will log in."
/// That order is enforced here rather than only drawn on screen — the incoming operator has no
/// shift at all until this is finished, so there is nothing to trade against.
/// </summary>
public class ShiftTakeoverService : IShiftTakeoverService
{
    private readonly AppDbContext _db;
    private readonly IAdminNotifier _notifier;
    private readonly IAuditService _audit;
    private readonly ILogger<ShiftTakeoverService> _logger;

    /// <summary>
    /// How long a shift has to sit untouched before the next operator is asked to take it over.
    ///
    /// This threshold exists because a branch can legitimately have two operators logged in at
    /// once — Katargam and Varachha have three counter machines each, and one shift per counter
    /// is deliberately not enforced until the EXE gives each counter a real identity. So "another
    /// operator's shift is open" cannot mean "abandoned", or logging in at the second counter
    /// would close the drawer out from under the person working at the first.
    ///
    /// Two hours, and the number is deliberately generous, because the two mistakes cost very
    /// different amounts. Too short and a real operator on a quiet morning is closed out by a
    /// colleague and has to log in again — worse, a branch that genuinely runs two counters is
    /// blocked from opening the second one. Too long and an abandoned shift dangles until the
    /// automatic day close picks it up at 06:00, which is the behaviour that already exists today
    /// and already sends the owner a report. Only one of those stops the shop.
    ///
    /// Two hours with nothing recorded at all — no session touched, no bill, no cash movement, no
    /// audited action anywhere — is an empty chair rather than a slow afternoon. The abandonment
    /// cases this is for are hours long: somebody walked out, or a PC died and was found at the
    /// next shift change.
    /// </summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(2);

    public ShiftTakeoverService(
        AppDbContext db,
        IAdminNotifier notifier,
        IAuditService audit,
        ILogger<ShiftTakeoverService> logger)
    {
        _db = db;
        _notifier = notifier;
        _audit = audit;
        _logger = logger;
    }

    public async Task<PendingTakeoverDto?> GetPendingAsync(Guid branchId, Guid operatorId, CancellationToken ct = default)
    {
        // A handover already half done comes first. Its count is on record and cannot be taken
        // again — the operator owes an explanation, not another number.
        var inProgress = await _db.ShiftHandovers
            .Where(h => h.BranchId == branchId
                     && h.CountedByOperatorId == operatorId
                     && h.Status == ShiftHandoverStatus.AwaitingReason)
            .OrderByDescending(h => h.CountedAt)
            .FirstOrDefaultAsync(ct);

        if (inProgress != null)
        {
            var outgoing = await _db.Shifts.FirstOrDefaultAsync(s => s.Id == inProgress.OutgoingShiftId, ct);
            var name = await OperatorNameAsync(inProgress.OutgoingOperatorId, ct);

            return new PendingTakeoverDto
            {
                Stage = TakeoverStages.Reason,
                OutgoingShiftId = inProgress.OutgoingShiftId,
                OutgoingOperatorName = name,
                StartedAt = outgoing?.LoginTime ?? inProgress.CountedAt,
                LastSeenAt = inProgress.CountedAt,
                UnattendedMinutes = inProgress.UnattendedMinutes,
                HasOpenDrawer = inProgress.CashRegisterId != null,
                Comparison = new TakeoverComparisonDto
                {
                    ExpectedCash = inProgress.ExpectedCash,
                    CountedCash = inProgress.CountedCash,
                    CashDifference = inProgress.CashDifference,
                    StockDifferences = ReadStockDifferences(inProgress.StockDifferences),
                    OutgoingOperatorName = name,
                },
            };
        }

        var abandoned = await FindAbandonedAsync(branchId, operatorId, ct);
        if (abandoned.Count == 0) return null;

        var oldest = abandoned[0];
        var register = await FindOpenDrawerAsync(branchId, ct);

        return new PendingTakeoverDto
        {
            Stage = TakeoverStages.Count,
            OutgoingShiftId = oldest.Shift.Id,
            OutgoingOperatorName = await OperatorNameAsync(oldest.Shift.OperatorId, ct),
            StartedAt = oldest.Shift.LoginTime,
            LastSeenAt = oldest.LastSeen,
            UnattendedMinutes = (int)(DateTimeOffset.UtcNow - oldest.LastSeen).TotalMinutes,
            AlsoClosing = abandoned.Count - 1,
            HasOpenDrawer = register != null,
            // Names only. What the system thinks is on the shelf is withheld for the same
            // reason as the cash figure: a count that can be matched to the answer is not a count.
            StockItems = await _db.InventoryItems.AsNoTracking()
                .Where(i => i.BranchId == branchId && i.Status != FoodAvailability.Disabled)
                .OrderBy(i => i.Category).ThenBy(i => i.ItemName)
                .Select(i => new TakeoverStockItemDto { Id = i.Id, Name = i.ItemName, Category = i.Category })
                .ToListAsync(ct),
        };
    }

    public async Task<TakeoverCountResultDto> SubmitCountAsync(
        Guid branchId, Guid operatorId, SubmitTakeoverCountDto dto, CancellationToken ct = default)
    {
        // Counting twice is how a figure gets "corrected" to match what the system expected.
        // A count already on record is handed straight back instead.
        var existing = await _db.ShiftHandovers
            .Where(h => h.BranchId == branchId
                     && h.CountedByOperatorId == operatorId
                     && h.Status == ShiftHandoverStatus.AwaitingReason)
            .OrderByDescending(h => h.CountedAt)
            .FirstOrDefaultAsync(ct);

        if (existing != null)
        {
            return new TakeoverCountResultDto
            {
                Completed = false,
                Comparison = new TakeoverComparisonDto
                {
                    ExpectedCash = existing.ExpectedCash,
                    CountedCash = existing.CountedCash,
                    CashDifference = existing.CashDifference,
                    StockDifferences = ReadStockDifferences(existing.StockDifferences),
                    OutgoingOperatorName = await OperatorNameAsync(existing.OutgoingOperatorId, ct),
                },
            };
        }

        var abandoned = await FindAbandonedAsync(branchId, operatorId, ct);
        if (abandoned.Count == 0)
            throw new AppException(
                "There is no shift left open here to take over.",
                System.Net.HttpStatusCode.Conflict, "NO_PENDING_TAKEOVER");

        var oldest = abandoned[0];
        var register = await FindOpenDrawerAsync(branchId, ct);

        // No drawer open means nobody put money in before walking off. Counting nothing and
        // recording a count of zero would read later as "the drawer was emptied".
        var expectedCash = register?.ExpectedDrawerCash ?? 0m;
        var countedCash = register is null ? 0m : dto.CountedCash;

        var stockDifferences = await CompareStockAsync(branchId, dto.StockCounts, ct);

        var now = DateTimeOffset.UtcNow;
        var handover = new ShiftHandover
        {
            Id = Guid.NewGuid(),
            BranchId = branchId,
            OutgoingShiftId = oldest.Shift.Id,
            OutgoingOperatorId = oldest.Shift.OperatorId,
            CountedByOperatorId = operatorId,
            CashRegisterId = register?.Id,
            ExpectedCash = expectedCash,
            CountedCash = countedCash,
            CashDifference = countedCash - expectedCash,
            StockDifferences = stockDifferences.Count == 0 ? null : JsonSerializer.Serialize(stockDifferences),
            UnattendedMinutes = (int)(now - oldest.LastSeen).TotalMinutes,
            Status = ShiftHandoverStatus.AwaitingReason,
            CountedAt = now,
            CreatedAt = now,
        };

        _db.ShiftHandovers.Add(handover);
        await _db.SaveChangesAsync(ct);

        var comparison = new TakeoverComparisonDto
        {
            ExpectedCash = expectedCash,
            CountedCash = countedCash,
            CashDifference = handover.CashDifference,
            StockDifferences = stockDifferences,
            OutgoingOperatorName = await OperatorNameAsync(oldest.Shift.OperatorId, ct),
        };

        // Everything agreed. There is nothing for the operator to explain, so making them type
        // a reason to get to work would be a form for the sake of a form.
        if (handover.CashDifference == 0 && stockDifferences.Count == 0)
        {
            var shiftId = await CompleteAsync(handover, reason: null, ct);
            return new TakeoverCountResultDto { Completed = true, ShiftId = shiftId, Comparison = comparison };
        }

        return new TakeoverCountResultDto { Completed = false, Comparison = comparison };
    }

    public async Task<TakeoverCompletedDto> ConfirmAsync(
        Guid branchId, Guid operatorId, ConfirmTakeoverDto dto, CancellationToken ct = default)
    {
        var handover = await _db.ShiftHandovers
            .Where(h => h.BranchId == branchId
                     && h.CountedByOperatorId == operatorId
                     && h.Status == ShiftHandoverStatus.AwaitingReason)
            .OrderByDescending(h => h.CountedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException(
                "There is no handover waiting to be finished. Count the drawer first.",
                System.Net.HttpStatusCode.Conflict, "NO_HANDOVER_IN_PROGRESS");

        var reason = dto.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            throw new AppException("Say what you think happened to the difference.");

        var shiftId = await CompleteAsync(handover, reason, ct);
        return new TakeoverCompletedDto { ShiftId = shiftId };
    }

    /// <summary>
    /// Closes the abandoned shift and its drawer, applies the stock count, starts the incoming
    /// operator's shift, and tells the owner. All of it, or none of it.
    /// </summary>
    private async Task<Guid> CompleteAsync(ShiftHandover handover, string? reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var incomingOperatorId = handover.CountedByOperatorId;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // The shift the count was taken against always closes, whatever has happened since —
        // the money in front of the incoming operator has already been counted and written down
        // against it, and leaving it open would leave that count attached to a live shift.
        var toClose = new List<Shift>();
        var outgoing = await _db.Shifts.FirstOrDefaultAsync(s => s.Id == handover.OutgoingShiftId, ct);
        if (outgoing is { Status: ShiftStatus.Active }) toClose.Add(outgoing);

        // Anything else still abandoned at this branch goes with it. There is one drawer and it
        // has just been counted once; leaving a second dangling shift behind would mean asking
        // the same person to count the same money again.
        foreach (var other in await FindAbandonedAsync(handover.BranchId, incomingOperatorId, ct))
        {
            if (other.Shift.Id != handover.OutgoingShiftId) toClose.Add(other.Shift);
        }

        foreach (var shift in toClose)
        {
            shift.LogoutTime = now;
            shift.Status = ShiftStatus.Completed;

            // The trap this whole feature turns on: whose shift it was, and who closed it, are
            // two different people and two different columns.
            shift.ClosedByOperatorId = incomingOperatorId;

            var op = await _db.Operators.FirstOrDefaultAsync(o => o.Id == shift.OperatorId, ct);
            if (op != null && op.Status == OperatorStatus.Active)
            {
                op.Status = OperatorStatus.LoggedOut;
                op.IsOnline = false;
            }
        }

        if (handover.CashRegisterId is { } registerId)
        {
            var register = await _db.CashRegisters.FirstOrDefaultAsync(r => r.Id == registerId, ct);
            if (register != null && register.Status != CashRegisterStatus.Closed)
            {
                register.PhysicalCashCounted = handover.CountedCash;
                register.CashDifference = handover.CashDifference;
                register.CountedByOperatorId = incomingOperatorId;
                register.MismatchReason = DescribeClosure(handover, reason);
                register.Status = CashRegisterStatus.Closed;
                register.VerifiedAt = handover.CountedAt;
                register.ClosedAt = now;
            }
        }

        await ApplyStockCountAsync(handover, incomingOperatorId, now, ct);

        // Only now does the incoming operator get a shift. Until this line they have a login and
        // nothing to trade with, which is what makes the count unavoidable rather than advisory.
        var newShift = new Shift
        {
            Id = Guid.NewGuid(),
            OperatorId = incomingOperatorId,
            BranchId = handover.BranchId,
            LoginTime = now,
            Status = ShiftStatus.Active,
            CreatedAt = now,
        };
        _db.Shifts.Add(newShift);

        handover.IncomingShiftId = newShift.Id;
        handover.Reason = reason;
        handover.Status = ShiftHandoverStatus.Completed;
        handover.CompletedAt = now;

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var incomingName = await OperatorNameAsync(incomingOperatorId, ct);
        var outgoingName = await OperatorNameAsync(handover.OutgoingOperatorId, ct);
        var branchName = await BranchNameAsync(handover.BranchId, ct);

        _logger.LogWarning(
            "{Incoming} closed {Outgoing}'s shift at {Branch}, unattended {Minutes} minutes. " +
            "Drawer counted at Rs {Counted} against Rs {Expected} expected.",
            incomingName, outgoingName, branchName,
            handover.UnattendedMinutes, handover.CountedCash, handover.ExpectedCash);

        await _audit.LogAsync(new AuditEntry
        {
            OperatorId = incomingOperatorId,
            UserRole = "Operator",
            UserName = incomingName,
            Action = AuditActions.ShiftTakeover,
            BranchId = handover.BranchId,
            BranchName = branchName,
            TargetType = "shift",
            TargetId = handover.OutgoingShiftId,
            Details = new
            {
                handoverId = handover.Id,
                closedShifts = toClose.Select(s => s.Id).ToArray(),
                outgoingOperator = outgoingName,
                expectedCash = handover.ExpectedCash,
                countedCash = handover.CountedCash,
                difference = handover.CashDifference,
                unattendedMinutes = handover.UnattendedMinutes,
                reason,
                newShiftId = newShift.Id,
            },
        });

        await NotifyOwnerAsync(handover, incomingName, outgoingName, branchName, reason, toClose.Count, ct);

        return newShift.Id;
    }

    /// <summary>
    /// Writes the counted quantities onto the stock, and a discrepancy log for each item that
    /// moved. Only items that differed are touched — the rest were already right.
    /// </summary>
    private async Task ApplyStockCountAsync(
        ShiftHandover handover, Guid incomingOperatorId, DateTimeOffset now, CancellationToken ct)
    {
        var differences = ReadStockDifferences(handover.StockDifferences);
        if (differences.Count == 0) return;

        var ids = differences.Select(d => d.InventoryId).ToList();
        var items = await _db.InventoryItems
            .Where(i => ids.Contains(i.Id) && i.BranchId == handover.BranchId)
            .ToListAsync(ct);

        var missing = new List<(InventoryItem Item, int Missing)>();

        foreach (var difference in differences)
        {
            var item = items.FirstOrDefault(i => i.Id == difference.InventoryId);
            if (item == null) continue;

            var oldStock = item.CurrentStock;
            if (difference.Counted < oldStock) missing.Add((item, oldStock - difference.Counted));
            item.CurrentStock = difference.Counted;
            item.UpdatedAt = now;

            if (difference.Counted == 0)
                item.Status = FoodAvailability.OutOfStock;
            else if (item.Status == FoodAvailability.OutOfStock)
                item.Status = FoodAvailability.Available;

            _db.InventoryLogs.Add(new InventoryLog
            {
                Id = Guid.NewGuid(),
                InventoryId = item.Id,
                BranchId = item.BranchId,
                OperatorId = incomingOperatorId,
                Action = "discrepancy",
                Quantity = difference.Counted - oldStock,
                OldValue = oldStock.ToString(),
                NewValue = difference.Counted.ToString(),
                Reason = "Counted while taking over a shift that was left open.",
                CreatedAt = now,
            });
        }

        // Missing on handover: the operator who left it pays (StockShortageBilling) - only where
        // Super Admin has switched that on for this branch.
        var chargeMissing = await _db.Branches.Where(b => b.Id == handover.BranchId)
            .Select(b => b.ChargeMissingStock).FirstOrDefaultAsync(ct);
        var outgoingName = await _db.Operators.Where(o => o.Id == handover.OutgoingOperatorId)
            .Select(o => o.FullName).FirstOrDefaultAsync(ct);
        if (chargeMissing && outgoingName is not null
            && StockShortageBilling.Build(handover.BranchId, handover.OutgoingOperatorId, outgoingName,
                handover.OutgoingShiftId, missing, now) is { } bill)
        {
            _db.Bills.Add(bill);
        }
    }

    /// <summary>
    /// Shifts at this branch that belong to somebody else and have had nothing happen on them
    /// for <see cref="AbandonedAfter"/>. Oldest first.
    /// </summary>
    private async Task<List<(Shift Shift, DateTimeOffset LastSeen)>> FindAbandonedAsync(
        Guid branchId, Guid excludeOperatorId, CancellationToken ct)
    {
        var candidates = await _db.Shifts
            .Where(s => s.BranchId == branchId
                     && s.Status == ShiftStatus.Active
                     && s.OperatorId != excludeOperatorId)
            .ToListAsync(ct);

        if (candidates.Count == 0) return new List<(Shift, DateTimeOffset)>();

        var shiftIds = candidates.Select(s => s.Id).ToList();
        var operatorIds = candidates.Select(s => s.OperatorId).Distinct().ToList();

        var lastSession = await _db.Sessions.AsNoTracking()
            .Where(s => s.ShiftId != null && shiftIds.Contains(s.ShiftId.Value))
            .GroupBy(s => s.ShiftId!.Value)
            .Select(g => new { ShiftId = g.Key, Last = g.Max(s => s.UpdatedAt) })
            .ToDictionaryAsync(x => x.ShiftId, x => x.Last, ct);

        // The audit trail is the broader signal: it catches a bill, a wallet top-up, a cash
        // adjustment, a PC put into maintenance — an operator busy at a quiet moment with no
        // session changing. Without it a slow Tuesday afternoon at the counter would look
        // exactly like an empty one.
        var lastAudit = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.OperatorId != null
                     && operatorIds.Contains(a.OperatorId.Value)
                     && a.BranchId == branchId)
            .GroupBy(a => a.OperatorId!.Value)
            .Select(g => new { OperatorId = g.Key, Last = g.Max(a => a.CreatedAt) })
            .ToDictionaryAsync(x => x.OperatorId, x => x.Last, ct);

        var now = DateTimeOffset.UtcNow;
        var abandoned = new List<(Shift Shift, DateTimeOffset LastSeen)>();

        foreach (var shift in candidates)
        {
            var lastSeen = shift.LoginTime;
            if (lastSession.TryGetValue(shift.Id, out var session) && session > lastSeen) lastSeen = session;
            if (lastAudit.TryGetValue(shift.OperatorId, out var audit) && audit > lastSeen) lastSeen = audit;

            if (now - lastSeen >= AbandonedAfter) abandoned.Add((shift, lastSeen));
        }

        return abandoned.OrderBy(a => a.LastSeen).ToList();
    }

    /// <summary>
    /// The drawer on the counter, whoever opened it and whichever trading day it belongs to.
    ///
    /// Not scoped to today: an operator who walked off at 23:00 leaves yesterday's drawer open,
    /// and it is that drawer the incoming operator is standing in front of at 06:30. Counting
    /// today's instead would count a drawer that does not exist yet.
    /// </summary>
    private Task<CashRegister?> FindOpenDrawerAsync(Guid branchId, CancellationToken ct) =>
        _db.CashRegisters
            .Where(r => r.BranchId == branchId && r.Status != CashRegisterStatus.Closed)
            .OrderByDescending(r => r.OpenedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Counted quantities against what the system holds. Items the operator did not send back
    /// are left alone rather than treated as zero — a partial submission must not empty a shelf.
    /// </summary>
    private async Task<List<TakeoverStockDifferenceDto>> CompareStockAsync(
        Guid branchId, List<TakeoverStockCountDto> counts, CancellationToken ct)
    {
        if (counts.Count == 0) return new List<TakeoverStockDifferenceDto>();

        var ids = counts.Select(c => c.InventoryId).ToList();
        var items = await _db.InventoryItems.AsNoTracking()
            .Where(i => ids.Contains(i.Id) && i.BranchId == branchId)
            .ToListAsync(ct);

        var differences = new List<TakeoverStockDifferenceDto>();

        foreach (var item in items)
        {
            var counted = counts.First(c => c.InventoryId == item.Id).Counted;
            if (counted == item.CurrentStock) continue;

            differences.Add(new TakeoverStockDifferenceDto
            {
                InventoryId = item.Id,
                ItemName = item.ItemName,
                Expected = item.CurrentStock,
                Counted = counted,
                Difference = counted - item.CurrentStock,
            });
        }

        return differences;
    }

    private static List<TakeoverStockDifferenceDto> ReadStockDifferences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<TakeoverStockDifferenceDto>();
        try
        {
            return JsonSerializer.Deserialize<List<TakeoverStockDifferenceDto>>(json)
                   ?? new List<TakeoverStockDifferenceDto>();
        }
        catch (JsonException)
        {
            return new List<TakeoverStockDifferenceDto>();
        }
    }

    /// <summary>What gets written on the drawer itself, so the register reads truthfully on its own.</summary>
    private static string DescribeClosure(ShiftHandover handover, string? reason)
    {
        var text = "Counted by the operator who came in next, not by the one whose shift it was — "
                 + "the shift was left open and was taken over.";
        return string.IsNullOrWhiteSpace(reason) ? text : $"{text} Their reason: {reason}";
    }

    private async Task NotifyOwnerAsync(
        ShiftHandover handover, string incomingName, string outgoingName, string branchName,
        string? reason, int shiftsClosed, CancellationToken ct)
    {
        try
        {
            var difference = handover.CashDifference;
            var isShort = difference < 0;
            var amount = Math.Abs(difference);
            var stockDifferences = ReadStockDifferences(handover.StockDifferences);
            var hasDrawer = handover.CashRegisterId != null;

            var rows = new List<(string Label, string Value)>
            {
                ("Branch", branchName),
                ("Shift left open by", outgoingName),
                ("Closed and counted by", incomingName),
                ("", ""),
                ("Last used", IndiaTime.Format(handover.CountedAt.AddMinutes(-handover.UnattendedMinutes))),
                ("Untouched for", AdminEmailTemplate.Describe(TimeSpan.FromMinutes(handover.UnattendedMinutes))),
                ("Counted at", IndiaTime.Format(handover.CountedAt)),
                ("", ""),
            };

            if (hasDrawer)
            {
                rows.Add(("Should have been in the drawer", $"Rs {handover.ExpectedCash:N2}"));
                rows.Add(("Actually counted", $"Rs {handover.CountedCash:N2}"));
                rows.Add((difference == 0 ? "Difference" : isShort ? "Missing" : "Extra",
                          difference == 0 ? "None - it matched" : $"Rs {amount:N2}"));
            }
            else
            {
                rows.Add(("Drawer", "None was open - there was no cash to count"));
            }

            if (stockDifferences.Count > 0)
            {
                rows.Add(("", ""));
                rows.Add(("Stock that did not match", $"{stockDifferences.Count} item{(stockDifferences.Count == 1 ? "" : "s")}"));
                foreach (var item in stockDifferences)
                    rows.Add(($"  {item.ItemName}", $"{item.Counted} counted, {item.Expected} expected ({item.Difference:+#;-#;0})"));
            }

            if (shiftsClosed > 1)
            {
                rows.Add(("", ""));
                rows.Add(("Shifts closed", $"{shiftsClosed} were left open and were all closed by this count"));
            }

            rows.Add(("", ""));
            rows.Add(("Reason given", string.IsNullOrWhiteSpace(reason) ? "Nothing to explain - everything matched" : reason));

            var headline = !hasDrawer
                ? null
                : difference == 0
                    ? $"Rs {handover.CountedCash:N2} counted"
                    : $"Rs {amount:N2} {(isShort ? "short" : "over")}";

            var accent = difference == 0 && stockDifferences.Count == 0
                ? AdminEmailTemplate.Amber
                : isShort ? AdminEmailTemplate.Red : AdminEmailTemplate.Amber;

            var subject = difference == 0
                ? $"{outgoingName}'s shift was left open at {branchName} - {incomingName} closed it"
                : $"Cash {(isShort ? "short" : "over")} by Rs {amount:N0} - {outgoingName}'s shift closed by {incomingName} at {branchName}";

            await _notifier.NotifyAsync(
                subject,
                AdminEmailTemplate.Compose(
                    heading: $"{outgoingName}'s shift was closed by somebody else",
                    accent: accent,
                    summary:
                        $"{outgoingName} never closed their shift at {branchName}. {incomingName} came in, "
                        + "counted the drawer and the stock, and closed it for them before starting their own shift.",
                    rows: rows,
                    headline: headline,
                    footnote:
                        $"The takings stay recorded against {outgoingName}'s shift, because they are theirs. "
                        + $"The count is recorded as {incomingName}'s, because {outgoingName} was not there to "
                        + "make it. Any difference above was found at handover and belongs to the shift that "
                        + "was left open, not to the operator who counted it."),
                ct);
        }
        catch (Exception ex)
        {
            // Never allowed to undo a completed handover. The shift is closed, the money is
            // counted and the next operator is working; failing on the email would lose all of it.
            _logger.LogError(ex, "Could not send the shift takeover email for handover {HandoverId}.", handover.Id);
        }
    }

    private async Task<string> OperatorNameAsync(Guid operatorId, CancellationToken ct) =>
        await _db.Operators.AsNoTracking()
            .Where(o => o.Id == operatorId)
            .Select(o => o.FullName)
            .FirstOrDefaultAsync(ct) ?? "Unknown operator";

    private async Task<string> BranchNameAsync(Guid branchId, CancellationToken ct) =>
        await _db.Branches.AsNoTracking()
            .Where(b => b.Id == branchId)
            .Select(b => b.Name)
            .FirstOrDefaultAsync(ct) ?? "Unknown branch";
}
