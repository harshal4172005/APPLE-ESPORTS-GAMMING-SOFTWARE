using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AppleEsportsErp.Application.DTOs.Common;
using AppleEsportsErp.Infrastructure.Data;
using AppleEsportsErp.Domain.Entities;
using AppleEsportsErp.Domain.Enums;
using AppleEsportsErp.Application.Interfaces;
using AppleEsportsErp.Application.Services;
using AppleEsportsErp.Api.Services;

namespace AppleEsportsErp.Api.Controllers;

/// <summary>
/// Head Office's inbox — receives everything the branches did while they were on their own.
/// </summary>
[ApiController]
[Route("api/sync")]
public class SyncInboxController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEmailService _email;
    private readonly ILogger<SyncInboxController> _logger;

    public SyncInboxController(
        AppDbContext db, IEmailService email, ILogger<SyncInboxController> logger)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    /// <summary>
    /// Receives a batch of events from a branch.
    ///
    /// Every event is committed to the inbox verbatim <b>before</b> any attempt is made to
    /// interpret it. That ordering is the whole point: a branch marks its entries delivered
    /// once we answer 200, so acknowledging data we did not actually keep destroys it for
    /// good. The previous implementation did exactly that — every handler was either a no-op
    /// or looked up a record Head Office had never been sent, so batches were acknowledged
    /// and silently discarded.
    /// </summary>
    [HttpPost("receive")]
    [AllowAnonymous]
    public async Task<IActionResult> ReceiveSyncBatch([FromBody] ReceiveSyncBatchDto dto)
    {
        if (dto?.Entries == null || dto.Entries.Count == 0)
        {
            _logger.LogWarning("Received empty sync batch from branch {BranchId}", dto?.BranchId);
            return Ok(ApiResponse<object>.Ok(new { processed = 0, total = 0, branchId = dto?.BranchId }));
        }

        try
        {
            var receivedAt = DateTimeOffset.UtcNow;

            // Redelivery is normal: a branch that never saw our response will send again.
            //
            // Held is not the same as applied, and treating it as such lost records for good.
            // An entry that was stored but failed to fold in was skipped on every resend, so
            // one failure became permanent — the branch was told "delivered", stopped
            // resending, and the session never appeared at Head Office. Anything not yet
            // applied is retried instead.
            var incomingIds = dto.Entries.Select(e => e.Id).ToList();
            var held = await _db.SyncInboxEntries
                .Where(e => incomingIds.Contains(e.Id))
                .ToListAsync();

            var alreadyApplied = held.Where(e => e.Applied).Select(e => e.Id).ToHashSet();
            var awaitingRetry = held.Where(e => !e.Applied).ToList();

            var stored = new List<SyncInboxEntry>();

            foreach (var entry in dto.Entries)
            {
                if (alreadyApplied.Contains(entry.Id)) continue;
                if (awaitingRetry.Any(h => h.Id == entry.Id)) continue;   // retried below

                stored.Add(new SyncInboxEntry
                {
                    Id = entry.Id,
                    BranchId = dto.BranchId,
                    AggregateType = entry.AggregateType ?? "Unknown",
                    AggregateId = entry.AggregateId,
                    EventType = entry.EventType ?? "unknown",
                    EventData = JsonSerializer.Serialize(entry.EventData),
                    // Normalised to UTC on the way in. Postgres "timestamp with time zone"
                    // accepts nothing else, and a branch in Surat legitimately sends +05:30 —
                    // left alone it takes down the whole batch with an Npgsql ArgumentException.
                    // This is the boundary where untrusted timestamps arrive, so it is the
                    // right place to fix them rather than trusting every branch to send UTC.
                    OccurredAt = entry.CreatedAt.ToUniversalTime(),
                    ReceivedAt = receivedAt,
                    Applied = false,
                });
            }

            if (stored.Count > 0)
            {
                _db.SyncInboxEntries.AddRange(stored);
                // Durable first. Nothing below may cost us what we have just accepted.
                await _db.SaveChangesAsync();
            }

            var appliedCount = 0;

            // Applied in dependency order, not arrival order. A branch captures a shift and its
            // cash register as two independent outbox entries, and within one batch nothing
            // guaranteed the shift landed first - CashRegister.ShiftId is a required column, so
            // when the register was processed first it hit FK_cash_register_shifts_ShiftId and
            // failed outright. Confirmed live: a register open, its own verification, and its
            // close all failed this way, every one landing seconds before the shift it named.
            // UpsertRowAsync already drops a *nullable* foreign key that has not arrived yet
            // (see its own remarks) - this is that same idea one level up, for the required ones
            // it cannot silently null out. Ties keep arrival order; OrderBy is a stable sort.
            foreach (var entry in stored.Concat(awaitingRetry).OrderBy(e => SyncApplyPriority(e.EventType)))
            {
                try
                {
                    await ApplyToHeadOfficeRecordsAsync(entry);
                    entry.Applied = true;
                    entry.ApplyError = null;

                    // Saved per entry, inside the try. Applying only queues changes — the
                    // insert happens here — so a single save for the whole batch threw
                    // outside this catch, took the entire request down with a 500, and left
                    // the error unrecorded. One bad entry must not cost the good ones.
                    await _db.SaveChangesAsync();
                    appliedCount++;
                }
                catch (Exception ex)
                {
                    // Whatever this entry queued is still tracked and would be retried by the
                    // next save, failing again and taking the rest of the batch with it.
                    DiscardPendingChanges();

                    entry.Applied = false;
                    entry.ApplyError = ex.GetBaseException().Message;

                    _logger.LogError(ex,
                        "Stored but could not apply sync entry {EntryId} ({EventType}) from branch {BranchId}",
                        entry.Id, entry.EventType, dto.BranchId);

                    try { await _db.SaveChangesAsync(); }
                    catch (Exception saveEx)
                    {
                        _logger.LogError(saveEx, "Could not even record why entry {EntryId} failed.", entry.Id);
                    }
                }
            }

            // Retries are counted separately from new arrivals. A batch that is all retries
            // and applies none of them is the signal that something is stuck, and lumping
            // them in with duplicates is what made this invisible in the first place.
            _logger.LogInformation(
                "Sync batch from branch {BranchId}: {Received} received, {Stored} new, {Retried} retried, {Applied} applied, {Duplicate} already applied.",
                dto.BranchId, dto.Entries.Count, stored.Count, awaitingRetry.Count, appliedCount, alreadyApplied.Count);

            return Ok(ApiResponse<object>.Ok(new
            {
                processed = appliedCount,
                stored = stored.Count,
                retried = awaitingRetry.Count,
                duplicates = alreadyApplied.Count,
                total = dto.Entries.Count,
                branchId = dto.BranchId
            }));
        }
        catch (Exception ex)
        {
            // Fail loudly. A 500 makes the branch keep the batch and retry, which is the safe
            // outcome — a repeated delivery is recoverable, a lost one is not.
            _logger.LogError(ex, "Critical error in sync inbox receiver for branch {BranchId}", dto.BranchId);
            return StatusCode(500, ApiResponse<object>.Fail("Sync processing failed", "SYNC_ERROR"));
        }
    }

    /// <summary>
    /// The other half of "make sure both sides are the same": everything above this line only
    /// ever asks "did a delivery attempt happen for this row". It says nothing about whether a
    /// row that was already delivered and applied still agrees with what the branch holds right
    /// now - a later correction to an already-closed register, or any future code path that
    /// changes a watched row without going through whatever SyncCapture is hooked into, would
    /// leave this side holding a stale copy forever with nothing anywhere flagging it.
    ///
    /// A branch periodically fingerprints every row it has recently cared about and sends the
    /// fingerprints here (see SyncManifestReconcilerService). This compares each one against
    /// Head Office's own copy of the same row and hands back exactly the rows that disagree -
    /// missing entirely, or present but different - so the branch can resend just those, in
    /// full, without either side ever transmitting rows that already match.
    /// </summary>
    [HttpPost("reconcile")]
    [AllowAnonymous]
    public async Task<IActionResult> ReconcileManifest([FromBody] ReconcileManifestDto dto)
    {
        if (dto?.Entries == null || dto.Entries.Count == 0)
            return Ok(ApiResponse<ReconcileResultDto>.Ok(new ReconcileResultDto()));

        var mismatched = new List<ReconcileMismatchDto>();

        foreach (var entry in dto.Entries)
        {
            var type = SyncCapture.TypeForAggregate(entry.AggregateType ?? "");
            if (type is null) continue;

            var current = await _db.FindAsync(type, entry.AggregateId);
            if (current is null)
            {
                mismatched.Add(new ReconcileMismatchDto
                {
                    AggregateType = entry.AggregateType!,
                    AggregateId = entry.AggregateId,
                    Reason = "missing",
                });
                continue;
            }

            var ourChecksum = SyncCapture.ComputeChecksum(_db.Entry(current));
            if (!string.Equals(ourChecksum, entry.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                mismatched.Add(new ReconcileMismatchDto
                {
                    AggregateType = entry.AggregateType!,
                    AggregateId = entry.AggregateId,
                    Reason = "different",
                });
            }
        }

        if (mismatched.Count > 0)
        {
            _logger.LogWarning(
                "Reconciliation with branch {BranchId}: checked {Checked}, {Count} row(s) disagree: {Rows}",
                dto.BranchId, dto.Entries.Count, mismatched.Count,
                string.Join(", ", mismatched.Select(m => $"{m.AggregateType}:{m.AggregateId} ({m.Reason})")));
        }
        else
        {
            _logger.LogInformation(
                "Reconciliation with branch {BranchId}: checked {Checked} row(s), all agree.",
                dto.BranchId, dto.Entries.Count);
        }

        return Ok(ApiResponse<ReconcileResultDto>.Ok(new ReconcileResultDto { Mismatched = mismatched }));
    }

    /// <summary>
    /// The row-level checks above answer "does the data agree". This answers the question one
    /// layer up: "would the two screens actually show the same thing" - the exact headline
    /// numbers a branch's own dashboard displays (PC states, who is on duty, today's EOD
    /// totals), computed here from Head Office's own mirrored copy of this branch's data using
    /// the same EOD service the branch itself calls.
    ///
    /// A branch compares this against its own local answer to the same question. If the two
    /// disagree even though ReconcileManifest finds no row-level mismatch, the underlying data
    /// genuinely agrees and something in how one side is computing or displaying it does not -
    /// a real bug to go looking for, not something more syncing will fix. If ReconcileManifest
    /// also found a mismatch, this is just that fix still working its way through.
    /// </summary>
    [HttpGet("parity-snapshot")]
    [AllowAnonymous]
    public async Task<IActionResult> ParitySnapshot([FromQuery] Guid branchId, [FromServices] IEodService eodService)
    {
        var snapshot = await BuildParitySnapshotAsync(_db, eodService, branchId);
        return Ok(ApiResponse<ParitySnapshotDto>.Ok(snapshot));
    }

    /// <summary>
    /// Shared by <see cref="ParitySnapshot"/> so this is never a second, subtly different
    /// formula from whichever one actually ends up wrong - a branch calls the equivalent local
    /// version of this exact method, not a hand-rolled comparison shaped for this endpoint alone.
    /// </summary>
    public static async Task<ParitySnapshotDto> BuildParitySnapshotAsync(
        AppDbContext db, IEodService eodService, Guid branchId)
    {
        var pcCounts = await db.Pcs.AsNoTracking()
            .Where(p => p.BranchId == branchId && !p.IsDeleted && p.State != PcState.AwaitingSetup)
            .GroupBy(p => p.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();

        int CountOf(PcState s) => pcCounts.FirstOrDefault(x => x.State == s)?.Count ?? 0;

        var operatorsOnDuty = await db.Shifts.AsNoTracking()
            .CountAsync(s => s.BranchId == branchId && s.Status == ShiftStatus.Active);

        var today = IndiaTime.BusinessDayOf(DateTimeOffset.UtcNow);
        var eod = await eodService.GenerateEodReportAsync(branchId, today);

        return new ParitySnapshotDto
        {
            TotalPcs = pcCounts.Sum(x => x.Count),
            PcsActive = CountOf(PcState.Active),
            PcsAwaitingBilling = CountOf(PcState.AwaitingBilling),
            PcsIdle = CountOf(PcState.Idle),
            PcsMaintenance = CountOf(PcState.UnderMaintenance),
            OperatorsOnDuty = operatorsOnDuty,
            EodNetRevenue = eod.Revenue.NetRevenue,
            EodGamingRevenue = eod.Revenue.TotalGamingRevenue,
            EodFoodRevenue = eod.Revenue.TotalFoodRevenue,
            EodCashTotal = eod.PaymentMethods.TotalCash,
            EodOnlineTotal = eod.PaymentMethods.TotalOnline,
            ExpectedDrawerCash = eod.Cash.ExpectedCashInDrawer,
        };
    }

    /// <summary>
    /// Re-attempts every currently unapplied entry, from any branch, regardless of when it
    /// arrived.
    ///
    /// <see cref="ReceiveSyncBatch"/> only retries an entry when its own branch resends the
    /// exact same batch id, which happens on a dropped response - not on a genuine dependency
    /// gap. A shift that was captured late leaves its cash register stuck for good otherwise:
    /// the branch already got its 200 OK for that entry and has no reason to ever send it
    /// again. Called on a timer by <c>SyncInboxRetryService</c>, independent of anything a
    /// branch does next.
    /// </summary>
    public async Task<int> RetryUnappliedEntriesAsync(CancellationToken ct)
    {
        // Applied=true only ever gets set alongside ApplyError=null (see the two success paths
        // above) — so a row holding both is a state current code cannot produce and never a
        // false alarm to retry again. It is how one specific entry got stuck for good in the
        // past: it was inconsistently left Applied=true with a real ApplyError already recorded,
        // which is invisible to a query that only looks for Applied=false. Catching that shape
        // here as well means a stale row like that heals itself the moment this sweep next
        // finds its missing dependency, instead of staying stuck forever.
        var pending = await _db.SyncInboxEntries.Where(e => !e.Applied || e.ApplyError != null).ToListAsync(ct);
        var appliedCount = 0;

        foreach (var entry in pending.OrderBy(e => SyncApplyPriority(e.EventType)).ThenBy(e => e.OccurredAt))
        {
            try
            {
                await ApplyToHeadOfficeRecordsAsync(entry);
                entry.Applied = true;
                entry.ApplyError = null;
                await _db.SaveChangesAsync(ct);
                appliedCount++;
            }
            catch (Exception ex)
            {
                DiscardPendingChanges();
                entry.ApplyError = ex.GetBaseException().Message;
                try { await _db.SaveChangesAsync(ct); }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "Could not even record why entry {EntryId} failed on retry.", entry.Id);
                }
            }
        }

        return appliedCount;
    }

    /// <summary>
    /// Where an event type sits in the dependency chain - lower applies first.
    ///
    /// Only the relationships that have actually caused a stuck foreign key are encoded here;
    /// everything else defaults to the last tier rather than everyone guessing at a total order
    /// this codebase does not otherwise need. member.created and shift.changed lead because nearly
    /// everything else can name one. cash_transaction, bill.paid and wallet events follow their
    /// own row (cash_register, bill, member) rather than preceding it.
    /// </summary>
    private static int SyncApplyPriority(string eventType) => eventType.ToLowerInvariant() switch
    {
        "member.created" => 0,
        "shift.changed" => 0,

        // Leads for the same reason member.created does: nearly everything else in this
        // switch - sessions, payments, cash transactions - refuses to apply at all until the
        // operator it names already exists here. See SyncCapture.Watched's own note on why
        // this event exists.
        "operator.changed" => 0,

        // Behind member.created, ahead of everything money-shaped: it needs its member to exist
        // and nothing else needs it, so it neither blocks a batch nor gets blocked by one.
        "member.reset_requested" => 1,
        "member.updated" => 1,
        "member.balance_adjusted" => 1,

        "bill.changed" => 1,
        "reservation.changed" => 1,
        "session.started" => 1,

        "cash_register.changed" => 2,
        "customer_credit.changed" => 2,
        "member_topup_credit.changed" => 2,   // depends on member.created (tier 0)
        "session.stopped" => 2,
        "session.ended" => 2,
        "payment.changed" => 2,   // depends on bill.changed (tier 1)

        "cash_transaction.changed" => 3,
        "denomination_count.changed" => 3,   // depends on cash_register.changed (tier 2)
        "bill.paid" => 3,
        "payment.recorded" => 3,
        "wallet.topped_up" => 3,
        "wallet.bonus_given" => 3,
        "member.wallet_toppedup" => 3,
        "wallet.deducted" => 3,
        "food_order.placed" => 3,

        "food_order.status_changed" => 4,

        _ => 5,   // inventory_item.changed, inventory_stock_delta.changed, audit_log.changed,
                  // email.send_requested - independent of everything else
    };

    /// <summary>
    /// Folds a stored event into Head Office's own records so it appears on the dashboard.
    ///
    /// Throwing here is safe and expected: the payload is already committed, so a failure
    /// marks that one entry unapplied rather than losing it or rejecting the batch. The usual
    /// cause is a branch reporting against a PC or operator Head Office has no row for, which
    /// resolves once the branch is provisioned from Head Office and both share identifiers.
    /// </summary>
    private async Task ApplyToHeadOfficeRecordsAsync(SyncInboxEntry held)
    {
        using var payload = JsonDocument.Parse(held.EventData);
        var root = payload.RootElement;

        switch (held.EventType.ToLowerInvariant())
        {
            case "member.created":
                await UpsertMemberAsync(held, root);
                break;

            case "member.reset_requested":
                await ApplyMemberResetTokenAsync(held, root);
                break;

            // A member's profile edited at the branch after they were first created - most
            // commonly adding/correcting their email on a walk-in registration that started
            // with none. See MemberService.UpdateMemberAsync's own comment for the exact bug
            // this closes: Head Office's copy silently going stale forever.
            case "member.updated":
                await ApplyMemberUpdateAsync(held, root);
                break;

            // A member's wallet balance or lifetime stat changed at the branch - via
            // MemberService.AdminEditValuesAsync, whether that edit was made locally by a
            // branch admin or relayed here from a Head Office Super Admin. Neither path ever
            // told Head Office's own copy of this member before, so its dashboard kept showing
            // the balance from before the edit until something unrelated happened to resync it.
            case "member.balance_adjusted":
                await ApplyMemberBalanceAdjustAsync(held, root);
                break;

            // An operator created or edited at a branch's own counter - see SyncCapture.
            // Watched's note on why this exists. PasswordHash/AccessPin travel through
            // UpsertRowAsync the same as every other column here, which is no more exposure
            // than the existing downward direction already has: Head Office hands the same
            // two fields to every OTHER branch already, in the config reply every heartbeat
            // can receive.
            case "operator.changed":
                await UpsertOperatorAsync(held, root);
                break;

            case "session.started":
                await UpsertSessionStartedAsync(held, root);
                break;

            case "session.stopped":
            case "session.ended":
                await UpsertSessionStoppedAsync(held, root);
                break;

            case "bill.paid":
            case "payment.recorded":
                await RecordPaymentAsync(held, root);
                break;

            case "wallet.topped_up":
            case "member.wallet_toppedup":
                await RecordWalletTopUpAsync(held, root);
                break;

            case "wallet.deducted":
                await RecordWalletDeductionAsync(held, root);
                break;

            case "wallet.bonus_given":
                await RecordWalletBonusAsync(held, root);
                break;

            // The four End of Day is computed from. Head Office never had any of them, which
            // is why its figures could not match the counter's however the screens were fixed.
            // They arrive as whole-row snapshots from SyncCapture and are applied as upserts,
            // so an update landing before the insert it belongs to still produces the right
            // row - which happens whenever a branch has been offline.
            case "shift.changed":
                await UpsertRowAsync<Shift>(held, root);
                break;

            case "cash_register.changed":
                await UpsertCashRegisterAsync(held, root);
                break;

            case "cash_transaction.changed":
                await UpsertRowAsync<CashTransaction>(held, root);
                break;

            // The note/coin breakdown behind a register's counted total - see
            // SyncCapture.Watched for why Head Office's history never showed it before this.
            case "denomination_count.changed":
                await UpsertRowAsync<DenominationCount>(held, root);
                break;

            case "customer_credit.changed":
                await UpsertRowAsync<CustomerCredit>(held, root);
                break;

            case "member_topup_credit.changed":
                await UpsertRowAsync<MemberTopUpCredit>(held, root);
                break;

            // The cash/online/wallet breakdown a bill was actually settled with. See
            // SyncCapture.Watched for why this exists - "bill.paid" and "payment.recorded"
            // below are the dead handlers this replaces.
            case "payment.changed":
                await UpsertRowAsync<Payment>(held, root);
                break;

            // Bookings. Head Office's reservations screen queried an empty table until now -
            // Reservation was never in SyncCapture's watch list, so not one booking taken at
            // any branch had ever arrived. See SyncCapture.Watched.
            case "reservation.changed":
                await UpsertRowAsync<Reservation>(held, root);
                break;

            // Every bill, not just the settled ones. bill.paid still carries the payment
            // itself; this carries the bill's existence, which unpaid bills never had.
            case "bill.changed":
                await UpsertRowAsync<Bill>(held, root);
                break;

            // The menu as the branch actually has it. Head Office's copy used to be a record of
            // the last thing Head Office had said, not of what the shop is really selling, so a
            // price changed at the counter never showed up here and every sales report was
            // priced against a menu that branch had abandoned.
            // CurrentStock and SoldQty travel normally again as of the Admin-only Add Stock
            // flow. The earlier exclusion here was a symptom-level patch for a problem now
            // closed at its actual source: Head Office no longer independently invents a stock
            // figure anywhere (Create always starts at zero, Update never touches stock,
            // Reconcile is branch-only) - see InventoryController. With nothing at Head Office
            // left to protect from being overwritten, blocking this sync would only have
            // stopped the one thing an admin adding stock from Head Office actually needs:
            // Head Office's own screen reflecting the branch's real count once it lands.
            case "inventory_item.changed":
                await UpsertRowAsync<InventoryItem>(held, root);
                break;

            // Sent only by branches older than the Head Office fridge count (SharedStockGate).
            // Relaying these to the other branch is what looped between the two Adajan branches
            // on 20 Oct; the count lives here now, so they are kept and not passed on.
            case "inventory_stock_delta.changed":
                break;

            // Food orders never travelled up at all before this. A walk-in order's money
            // happened to arrive because its Bill is separately watched, but a session-linked
            // order updated nothing synced until the food was marked delivered - and even then
            // Head Office only ever learned a total, never which dishes or how many.
            case "food_order.placed":
                await UpsertFoodOrderPlacedAsync(held, root);
                break;

            case "food_order.status_changed":
                await ApplyFoodOrderStatusAsync(held, root);
                break;

            // Who did what, at any branch, readable at Head Office without needing to be
            // standing at that branch's own counter.
            case "audit_log.changed":
                await UpsertRowAsync<AuditLog>(held, root);
                break;

            case "email.send_requested":
                await SendQueuedEmailAsync(held, root);
                break;

            default:
                _logger.LogWarning("No handler for sync event type {EventType}; payload retained.", held.EventType);
                break;
        }
    }

    /// <summary>
    /// Forgets an optional reference Head Office does not have.
    ///
    /// A shift belongs to the branch that opened it and is never sent up — so a session
    /// naming one failed its foreign key, and the whole delivery was lost:
    ///
    ///     insert or update on table "sessions" violates foreign key
    ///     constraint "FK_sessions_shifts_ShiftId"
    ///
    /// Refusing the record over a detail Head Office does not track would be the wrong
    /// trade. Who played, on which PC, for how much, is what matters up here; which of the
    /// operator's shifts it fell in is branch bookkeeping. The session is kept and the
    /// reference dropped, rather than the reverse.
    /// </summary>
    /// <summary>
    /// Applies a whole-row snapshot from a branch, creating the row or updating it in place.
    ///
    /// Written once and reused for shifts, cash registers, cash transactions and credits
    /// rather than four near-identical handlers, because four hand-written copies is how the
    /// fifth one gets forgotten - the exact failure this whole piece of work is undoing.
    ///
    /// Two things make it safe against a branch that has been offline. It upserts, so an
    /// update that overtakes its own insert still lands correctly. And it drops any optional
    /// reference to a row Head Office does not have - a cash register naming a shift that has
    /// not arrived yet is kept, minus the reference, instead of being rejected outright and
    /// taking the day's takings with it.
    /// </summary>
    /// <summary>
    /// Same as <see cref="UpsertRowAsync{TEntity}"/>, with one extra step first: the branch is
    /// always the source of truth for its own drawer, so if Head Office is still holding a
    /// DIFFERENT non-closed register for this branch, that copy is stale by definition - the
    /// branch has moved on - and is closed here rather than left to collide with the incoming
    /// row.
    ///
    /// Before this, that collision (the "one open register per branch" rule, built precisely to
    /// stop a branch's own duplicate-register bug) refused the incoming row outright and retried
    /// the identical failure forever, with nothing able to break it. Confirmed live at
    /// Adajan-240Hz, 1 Oct 2026: one stale mirror register blocked roughly 19 hours of that
    /// branch's updates - every shift change, every cash movement - discovered only because the
    /// owner happened to notice the dashboard had stopped moving. Head Office never has a reason
    /// to insist its own copy is right once the branch says otherwise; it only ever has a reason
    /// to catch up.
    /// </summary>
    private async Task UpsertCashRegisterAsync(SyncInboxEntry held, JsonElement root)
    {
        // ExecuteUpdateAsync, not the change tracker - it runs immediately against the database,
        // so there is no ordering question with the upsert below. The change-tracker version of
        // this (stage the close, then SaveChangesAsync before the upsert) still failed live: EF
        // does not save in the order code calls things in, so the incoming register's INSERT
        // could run before the stale row's UPDATE in the same batch, hitting the exact
        // constraint this method exists to avoid. Executed directly, the stale row is actually
        // closed in the database before the next line even starts.
        await _db.Set<CashRegister>()
            .Where(r => r.BranchId == held.BranchId
                && r.Id != held.AggregateId
                && r.Status != CashRegisterStatus.Closed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, CashRegisterStatus.Closed)
                .SetProperty(r => r.ClosedAt, DateTimeOffset.UtcNow)
                .SetProperty(r => r.MismatchReason, r => r.MismatchReason ??
                    "Auto-closed: the branch reported a different register as its current one during sync, so this mirror copy was stale."));

        await UpsertRowAsync<CashRegister>(held, root);
    }

    private async Task UpsertOperatorAsync(SyncInboxEntry held, JsonElement root)
    {
        var username = ReadString(root, "Username");

        if (username is not null)
        {
            // Same shape of bug as UpsertCashRegisterAsync, different cause: a branch's
            // locally-seeded operator (almost always "System Administrator") can end up with a
            // new local Id after that branch's own database is reset or reinstalled, while the
            // username stays the same deterministic string. Head Office still has the old Id's
            // row under that exact username, so the new Id's insert below would collide on
            // IX_operators_Username forever - and because every shift/bill/credit/session this
            // operator touches afterwards carries the NEW Id as its OperatorId, every one of
            // those would also never resolve. One stuck operator row, not a dozen unrelated
            // sync bugs - confirmed live against the actual backlog, where the stale username
            // was the common ancestor of thousands of otherwise-unexplained failures. The old
            // row is renamed, not deleted - it is still the correct OperatorId on record for
            // whatever history already synced under it.
            // Username is varchar(50); the seeded "system_admin_<branch guid>" name alone can
            // already run to 49 characters, so appending anything to it overflows the column
            // (hit live: "22001: value too long for type character varying(50)" on the first
            // deploy of this fix). A short, fixed-length replacement - "stale_" plus the old
            // row's own Id (42 characters, always) - fits with room to spare and is still
            // unique per row.
            await _db.Set<Operator>()
                .Where(o => o.BranchId == held.BranchId
                    && o.Id != held.AggregateId
                    && o.Username == username)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    o => o.Username,
                    o => "stale_" + o.Id));
        }

        await UpsertRowAsync<Operator>(held, root);
    }

    private async Task UpsertRowAsync<TEntity>(
        SyncInboxEntry held, JsonElement root, IReadOnlySet<string>? excludeFields = null)
        where TEntity : class, new()
    {
        var set = _db.Set<TEntity>();
        var existing = await set.FindAsync(held.AggregateId);

        var entity = existing ?? new TEntity();
        var entry = existing is null ? _db.Entry(entity) : _db.Entry(existing);

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;

            // Never write a row version. xmin is PostgreSQL's own bookkeeping about a row in
            // THIS database; taking a branch's copy of it and writing it here is meaningless.
            if (property.Metadata.IsConcurrencyToken) continue;

            // Only truly computed columns are refused. A column merely having a default -
            // which is most of the money on a cash register - must still accept the branch's
            // value, or the till arrives reading zero.
            if (property.Metadata.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate)
                continue;

            // A field named by the caller as belonging to the branch alone, never to Head
            // Office. Exists because of exactly one confirmed failure: a menu item created at
            // Head Office was pushed down to a branch as a catalogue entry (deliberately
            // without a stock count, the branch's own concern); the branch created its own
            // local copy starting at zero, correctly, since it had genuinely never stocked it;
            // that zero then synced straight back up here and overwrote the number Head Office
            // had just been given. Proven on the live server, not a guess - the round trip's
            // own payload named the exact millisecond it happened. It is not a race that only
            // sometimes fires; it is the guaranteed outcome the first time the branch reports
            // in, for any item created or edited from Head Office. CurrentStock and SoldQty are
            // the branch's own trading state and must never travel this direction, the same
            // rule a PC's busy/idle state already follows.
            if (excludeFields?.Contains(name) == true) continue;

            if (!root.TryGetProperty(name, out var value)) continue;

            // The primary key is set from the branch's id when creating, and never touched
            // afterwards - an update must not be able to move a row to a different id.
            //
            // BranchId follows the same rule, for a reason that only started mattering once
            // two different branches could legitimately send a row sharing the same id (see
            // BranchHeartbeatService.ApplyOneMenuItemAsync, which deliberately reuses a shared
            // item's Guid across a food group's branches). Without this, whichever branch's
            // echo of that shared item happened to sync up last would silently flip who
            // Head Office thinks owns the row - and a generic upsert has no way to tell "this
            // branch legitimately owns it" apart from "this branch's echo just landed last".
            // Owning branch is decided once, at creation, exactly like the id itself.
            if ((property.Metadata.IsPrimaryKey() || name == "BranchId") && existing is not null) continue;

            var clr = Nullable.GetUnderlyingType(property.Metadata.ClrType) ?? property.Metadata.ClrType;

            object? converted;
            try
            {
                converted = ConvertJson(value, clr);
            }
            catch
            {
                continue;   // a field this build cannot read is skipped, not fatal
            }

            // An optional link to something Head Office has never seen - most often a shift,
            // which belongs to the branch and may arrive later or not at all.
            if (converted is Guid g && g != Guid.Empty
                && property.Metadata.IsForeignKey()
                && property.Metadata.IsNullable
                && !await ForeignRowExistsAsync(property.Metadata, g))
            {
                converted = null;
            }

            property.CurrentValue = converted;
        }

        if (existing is null)
        {
            entry.Property("Id").CurrentValue = held.AggregateId;
            set.Add(entity);
        }
    }

    /// <summary>
    /// Whether the row a foreign key points at actually exists here yet.
    ///
    /// Asked of the principal table named by the relationship rather than a hard-coded list,
    /// so a new nullable reference added to any of these entities is handled without anyone
    /// remembering to extend this.
    /// </summary>
    private async Task<bool> ForeignRowExistsAsync(Microsoft.EntityFrameworkCore.Metadata.IProperty property, Guid id)
    {
        var principal = property.GetContainingForeignKeys().FirstOrDefault()?.PrincipalEntityType.ClrType;
        if (principal is null) return true;   // cannot tell; leave the value alone

        var rows = (IQueryable<object>)_db.GetType()
            .GetMethod(nameof(DbContext.Set), 1, Type.EmptyTypes)!
            .MakeGenericMethod(principal)
            .Invoke(_db, null)!;

        return await rows.AnyAsync(e => EF.Property<Guid>(e, "Id") == id);
    }

    private static object? ConvertJson(JsonElement value, Type target)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;

        if (target == typeof(Guid)) return value.GetGuid();
        if (target == typeof(string)) return value.GetString();
        if (target == typeof(int)) return value.GetInt32();
        if (target == typeof(long)) return value.GetInt64();
        if (target == typeof(decimal)) return value.GetDecimal();
        if (target == typeof(double)) return value.GetDouble();
        if (target == typeof(bool)) return value.GetBoolean();
        if (target == typeof(DateTimeOffset)) return value.GetDateTimeOffset().ToUniversalTime();
        if (target == typeof(DateTime)) return value.GetDateTime().ToUniversalTime();
        if (target == typeof(DateOnly)) return DateOnly.FromDateTime(value.GetDateTime());

        // Enums travel as their name, so a value this build does not recognise is skipped by
        // the caller rather than silently becoming whichever member happens to be zero.
        if (target.IsEnum) return Enum.Parse(target, value.GetString()!, ignoreCase: true);

        return value.GetString();
    }

    private async Task<Guid?> KnownHereOnly<TEntity>(Guid? id) where TEntity : class
        => id is null || !await _db.Set<TEntity>().AnyAsync(e => EF.Property<Guid>(e, "Id") == id)
            ? null
            : id;

    /// <summary>
    /// Drops everything a failed entry queued, so the next save is not poisoned by it.
    /// Inbox rows are kept — they carry the record of what went wrong.
    /// </summary>
    private void DiscardPendingChanges()
    {
        foreach (var tracked in _db.ChangeTracker.Entries().ToList())
        {
            if (tracked.Entity is SyncInboxEntry) continue;
            if (tracked.State is EntityState.Added or EntityState.Modified)
                tracked.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// A member signed up at a branch, arriving at Head Office.
    ///
    /// Members are the one record a branch legitimately creates that Head Office must also
    /// hold: a person joins at whichever shop they walk into, and their wallet follows them to
    /// the others. Without this the branch's own wallet top-ups arrive naming somebody the
    /// server has never heard of - Rs 1,000 belonging to nobody.
    ///
    /// No balances are set from here. The amounts on this event are what the branch had at the
    /// moment of signing up, which is nothing; the money is a separate story told by wallet
    /// events, and inferring a balance from a creation event is how a stale batch overwrites a
    /// real one.
    /// </summary>
    /// <summary>
    /// Takes a reset token a branch has just minted, so the link in that member's email can be
    /// honoured here.
    ///
    /// The link points at Head Office because that is the only address that means anything in
    /// somebody's inbox - a branch's own is the shop LAN. The token, though, is created in the
    /// branch's local database, and Member updates were never part of sync, so Head Office had
    /// no way to recognise a member arriving with one. This is the field that closes that gap.
    ///
    /// Deliberately does not create the member. It rides behind member.created in the same
    /// dependency ordering the rest of this inbox uses - and a token for a member Head Office
    /// has never heard of is not something to invent a row for, it is something to wait for.
    /// </summary>
    private async Task ApplyMemberResetTokenAsync(SyncInboxEntry held, JsonElement root)
    {
        var memberId = held.AggregateId;
        var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == memberId);

        // Left unapplied on purpose rather than swallowed. SyncInboxRetryService comes back for
        // it, so a token that arrives before its member - a different batch, a slow first sync -
        // lands once the member does, instead of being lost with the customer holding a link
        // that will never work.
        if (member is null)
            throw new InvalidOperationException($"Member {memberId} is not known here yet; reset token deferred.");

        var token = ReadString(root, "resetToken");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"Reset token for member {memberId} arrived empty.");

        member.ResetToken = token;
        member.ResetTokenExpiry = root.TryGetProperty("resetTokenExpiry", out var exp)
            && exp.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow.AddHours(1);

        member.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task UpsertMemberAsync(SyncInboxEntry held, JsonElement root)
    {
        var memberId = held.AggregateId;
        if (await _db.Members.AnyAsync(m => m.Id == memberId))
            return;   // already known: a redelivery, or this box is both branch and Head Office

        var fullName = ReadString(root, "fullName");
        if (string.IsNullOrWhiteSpace(fullName))
            throw new InvalidOperationException($"Member {memberId} arrived with no name.");

        // Gameplay itself is deliberately not branch-scoped - a member joins at one shop and
        // plays at any of them, which is the whole reason their wallet lives at Head Office
        // rather than on one till. HomeBranchId below does not change that; nothing gates play
        // on it. It exists for a single, narrower purpose - CompletePasswordResetAsync and
        // WalletService's setup-token flow both need to know which branch's own copy of this
        // member actually gets checked at login, so they know where to send a changed password.
        // Leaving it null (as this used to) answers that question with "nowhere": Head Office
        // would validate a reset link, update its own copy, tell the customer "success" - and
        // never queue anything for any branch to receive, leaving the login every gaming PC
        // actually checks untouched. held.BranchId is exactly that answer, already sitting on
        // the envelope this event arrived in.
        _db.Members.Add(new Member
        {
            Id = memberId,
            FullName = fullName,
            MemberNumber = ReadString(root, "memberNumber") ?? memberId.ToString("N")[..8].ToUpperInvariant(),
            MobileNumber = ReadString(root, "mobileNumber") ?? string.Empty,
            Email = ReadString(root, "email"),
            Username = ReadString(root, "username"),
            // Without this, a member's very first password - set at the branch the moment they
            // registered - never reached Head Office at all. Head Office created its own copy
            // with no password, forever, and since members are shared across every branch, any
            // OTHER branch that later learned about this member inherited that same blank
            // password from Head Office - the member could only ever log in at the one branch
            // where they signed up. See MemberService.RegisterMemberAsync's own outbox event.
            PasswordHash = ReadString(root, "passwordHash"),
            PasswordChangedAt = ReadDate(root, "passwordChangedAt"),
            Status = MemberStatus.Active,
            HomeBranchId = held.BranchId,
            CreatedAt = ReadDate(root, "createdAt") ?? held.OccurredAt,
            UpdatedAt = held.ReceivedAt,
        });
    }

    /// <summary>
    /// Patches an existing Head Office member row with a branch-side profile edit - most
    /// commonly the email being added or corrected after a walk-in registration. Throws if the
    /// member isn't here yet rather than upserting a partial row: member.created carries the
    /// full profile and is a tier below this one, so it should already have arrived; if it
    /// genuinely hasn't, this entry is retried once it has, same as ApplyMemberResetTokenAsync's
    /// own reasoning for member.reset_requested.
    /// </summary>
    private async Task ApplyMemberUpdateAsync(SyncInboxEntry held, JsonElement root)
    {
        var memberId = held.AggregateId;
        var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == memberId)
            ?? throw new InvalidOperationException(
                $"Head Office has no member {memberId}. The member.created event should arrive first.");

        member.FullName = ReadString(root, "fullName") ?? member.FullName;
        member.MobileNumber = ReadString(root, "mobileNumber") ?? member.MobileNumber;
        member.Email = ReadString(root, "email");
        member.Username = ReadString(root, "username");

        // Same gap as UpsertMemberAsync's own note, just for an edit instead of a fresh
        // registration - an operator resetting a member's password at the counter (the normal,
        // in-person way "forgot password" gets handled) never told Head Office at all before
        // this. Guarded by Member.IsPasswordNewer the same way the downward push now is: only
        // move forward in time, so a late-arriving or redelivered event can never undo a
        // password that has already changed again since.
        var incomingPasswordHash = ReadString(root, "passwordHash");
        var incomingPasswordChangedAt = ReadDate(root, "passwordChangedAt");
        if (!string.IsNullOrWhiteSpace(incomingPasswordHash)
            && incomingPasswordHash != member.PasswordHash
            && member.IsPasswordNewer(incomingPasswordChangedAt))
        {
            member.SetPassword(incomingPasswordHash, incomingPasswordChangedAt);
        }

        member.UpdatedAt = ReadDate(root, "updatedAt") ?? held.ReceivedAt;
    }

    /// <summary>
    /// Patches Head Office's copy of a member's wallet balance and lifetime stats after
    /// MemberService.AdminEditValuesAsync changed them at the branch - see that method's own
    /// comment. Every field here is the branch's authoritative, post-edit value, not a delta,
    /// so this always lands correctly regardless of what Head Office's stale copy previously
    /// held. Throws (not upserts) if the member isn't here yet, same reasoning as
    /// ApplyMemberUpdateAsync just above: member.created is a lower tier and should already
    /// have landed.
    /// </summary>
    private async Task ApplyMemberBalanceAdjustAsync(SyncInboxEntry held, JsonElement root)
    {
        var memberId = held.AggregateId;
        var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == memberId)
            ?? throw new InvalidOperationException(
                $"Head Office has no member {memberId}. The member.created event should arrive first.");

        member.GamingBalance = ReadDecimal(root, "gamingBalance") ?? member.GamingBalance;
        member.FoodBalance = ReadDecimal(root, "foodBalance") ?? member.FoodBalance;
        member.TotalGamingTopUps = ReadDecimal(root, "totalGamingTopUps") ?? member.TotalGamingTopUps;
        member.TotalGamingBonusEarned = ReadDecimal(root, "totalGamingBonusEarned") ?? member.TotalGamingBonusEarned;
        member.TotalGamingSpend = ReadDecimal(root, "totalGamingSpend") ?? member.TotalGamingSpend;
        member.TotalFoodSpend = ReadDecimal(root, "totalFoodSpend") ?? member.TotalFoodSpend;
        member.GamingPoints = ReadInt(root, "gamingPoints") ?? member.GamingPoints;
        member.FoodPoints = ReadInt(root, "foodPoints") ?? member.FoodPoints;
        member.TotalPoints = ReadInt(root, "totalPoints") ?? member.TotalPoints;
        member.UpdatedAt = ReadDate(root, "updatedAt") ?? held.ReceivedAt;
    }

    /// <summary>
    /// Head Office's view of what each PC is doing, kept in step with the sessions it is told
    /// about.
    ///
    /// Derived from session events rather than reported separately, deliberately. A PC's state
    /// is set in a dozen places across billing, reservations and maintenance, and instrumenting
    /// every one of them means missing one — and a state that is right most of the time is worse
    /// than one that is plainly derived, because nobody knows which screens to trust.
    ///
    /// Before this, Head Office showed Adajan with three PCs "awaiting billing" whose last
    /// change was in July, against sessions that no longer existed. Four days of fiction on the
    /// screen the owner uses to see the business.
    /// </summary>
    /// <summary>
    /// No longer used, and deliberately so - see the note on the two calls that were removed.
    ///
    /// Kept only because a branch far enough behind to send events but no heartbeat would
    /// otherwise leave Head Office with no PC state at all. Nothing on any supported version
    /// reaches this.
    /// </summary>
    private async Task SetPcStateAsync(Guid? pcId, PcState state, Guid? currentSessionId)
    {
        if (pcId is null) return;

        var pc = await _db.Pcs.FirstOrDefaultAsync(p => p.Id == pcId);
        if (pc is null) return;

        pc.State = state;
        pc.CurrentSessionId = currentSessionId;
        pc.LastActiveAt = DateTimeOffset.UtcNow;
        pc.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Sends an email a branch could not send itself.
    ///
    /// Only Head Office holds the mail credentials, so a branch queues instead of sending — the
    /// welcome message, the password reset link, the low stock warning. That design was right
    /// and the last step was missing: these arrived here and fell through to "no handler",
    /// which logged a warning and marked the entry applied. Every email any branch has ever
    /// queued was accepted and thrown away, and the branch was told it had been delivered.
    ///
    /// Found because a member signed up at a branch and no reset link ever came.
    ///
    /// A failure to send throws, which leaves the entry unapplied and retried. Mail that
    /// bounces is a different problem from mail never attempted, and only the second is worth
    /// retrying.
    /// </summary>
    private async Task SendQueuedEmailAsync(SyncInboxEntry held, JsonElement root)
    {
        var to = ReadString(root, "to");
        var subject = ReadString(root, "subject");
        var body = ReadString(root, "body");

        if (string.IsNullOrWhiteSpace(to))
        {
            // Nothing to retry towards. Swallowed rather than thrown so it does not sit in the
            // queue for ever being reattempted at nobody.
            _logger.LogWarning("A branch queued an email with no recipient. Discarded.");
            return;
        }

        await _email.SendEmailAsync(to, subject ?? "Apple Esports", body ?? string.Empty);

        _logger.LogInformation(
            "Sent an email queued by branch {BranchId} to {Recipient}.", held.BranchId, to);
    }

    /// <summary>
    /// Money a branch actually took, appearing in Head Office's own figures.
    ///
    /// The previous version deliberately refused to do this, and its reasoning was sound as far
    /// as it went: a bill is a tree of line items and discounts, and rebuilding one from a flat
    /// event produces something that looks like a bill and does not reconcile. But refusing
    /// meant the entry was marked applied while nothing was written, so a branch's takings never
    /// reached the End of Day screen and nothing anywhere said so. Head Office showed a shop
    /// that traded all evening as having taken nothing.
    ///
    /// The distinction that resolves it: a PAYMENT is flat. Cash, online, wallet, when, which
    /// branch - facts, not a tree. That is also exactly what the End of Day screen reads. So the
    /// payment is recorded in full, and the bill it belongs to is written as a header carrying
    /// the branch's own totals, with no invented line items. The items stay at the branch, which
    /// is where they were rung up and where they can be looked at.
    ///
    /// The branch's own identifiers are used throughout, so the same bill is one bill in both
    /// places and a redelivery cannot double the takings.
    /// </summary>
    private async Task RecordPaymentAsync(SyncInboxEntry held, JsonElement root)
    {
        var billId = ReadGuid(root, "billId") ?? held.AggregateId;

        if (await _db.Payments.AnyAsync(p => p.BillId == billId))
            return;   // already counted

        var operatorId = ReadGuid(root, "operatorId");
        if (operatorId is null || !await _db.Operators.AnyAsync(o => o.Id == operatorId))
            throw new InvalidOperationException(
                $"Head Office has no operator {operatorId}, so this payment cannot be attributed.");

        var gaming = ReadDecimal(root, "gamingAmount") ?? 0m;
        var food = ReadDecimal(root, "foodAmount") ?? 0m;
        var discount = ReadDecimal(root, "discountAmount") ?? 0m;
        var billTotal = ReadDecimal(root, "billTotal") ?? (gaming + food - discount);
        var totalPaid = ReadDecimal(root, "totalPaid") ?? billTotal;
        var cash = ReadDecimal(root, "cashAmount") ?? 0m;
        var paidAt = ReadDate(root, "paidAt") ?? held.OccurredAt;

        Enum.TryParse<PaymentType>(ReadString(root, "paymentType"), ignoreCase: true, out var method);

        // Each leg as the branch actually took it.
        //
        // This used to be `nonCash = totalPaid - cash`, then assigned wholly to Online or wholly
        // to Wallet depending on the payment type - which cannot represent a Split at all. A
        // Rs 5 cash + Rs 5 online bill arrived here as Rs 10 with no idea where half of it went,
        // and the Bill row below was never given any of the legs, so Head Office booked the lot
        // as cash: drawer overstated by Rs 5, online settlement missing Rs 5, on every split
        // bill ever taken.
        //
        // Older branches predating this send only cashAmount. For them the old inference is
        // still the best available answer, so it is kept as the fallback rather than silently
        // recording zero - a 2.4.10 branch must not start reporting worse figures than it did.
        var legsPresent = root.TryGetProperty("onlineAmount", out _) || root.TryGetProperty("walletAmount", out _);
        var inferredNonCash = Math.Max(0m, totalPaid - cash);

        var online = legsPresent
            ? ReadDecimal(root, "onlineAmount") ?? 0m
            : (method == PaymentType.Wallet ? 0m : inferredNonCash);

        var wallet = legsPresent
            ? ReadDecimal(root, "walletAmount") ?? 0m
            : (method == PaymentType.Wallet ? inferredNonCash : 0m);

        var actualCash = ReadDecimal(root, "actualCashCollected") ?? cash;

        if (!await _db.Bills.AnyAsync(b => b.Id == billId))
        {
            _db.Bills.Add(new Bill
            {
                Id = billId,
                BillNumber = ReadString(root, "billNumber") ?? billId.ToString("N")[..8].ToUpperInvariant(),
                BranchId = held.BranchId,
                OperatorId = operatorId.Value,
                SessionId = await KnownHereOnly<Session>(ReadGuid(root, "sessionId")),
                ShiftId = await KnownHereOnly<Shift>(ReadGuid(root, "shiftId")),
                GamingAmount = gaming,
                FoodAmount = food,
                Subtotal = gaming + food,
                DiscountAmount = discount,
                TotalAmount = billTotal,
                PaymentType = method,
                // The Bill's own legs were never set here at all, so they defaulted to zero
                // while the Payment row beside them carried the real figures - two rows in one
                // database disagreeing about the same money. Every report that reads the bill
                // rather than the payment (End of Day's payment breakdown among them) was
                // reading those zeros.
                CashAmount = cash,
                OnlineAmount = online,
                WalletAmount = wallet,
                CashReceived = cash,
                ActualCashCollected = actualCash,
                Status = BillStatus.Completed,
                CreatedAt = paidAt,
                CompletedAt = paidAt,
            });
        }

        _db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            BillId = billId,
            BranchId = held.BranchId,
            OperatorId = operatorId.Value,
            PaymentType = method,
            TotalAmount = totalPaid,
            CashAmount = cash,
            OnlineAmount = online,
            WalletAmount = wallet,
            CashReceived = cash,
            ChangeReturned = 0m,
            ActualCashCollected = actualCash,
            GamingPortion = gaming,
            FoodPortion = food,
            Status = "completed",
            CreatedAt = paidAt,
        });
    }

    /// <summary>
    /// A wallet top-up taken at a branch, recorded at Head Office - and, now, actually applied.
    ///
    /// This used to record the transaction and deliberately leave the member's balance alone,
    /// reasoning that "the branch owns that figure". That was half right: a single event
    /// really cannot be trusted blindly, an out-of-order delivery really could corrupt real
    /// money. But leaving it alone forever was not the fix - it meant Head Office's wallet
    /// figure could only ever be zero or stale, which is exactly the "Members - wallet not in
    /// sync" report this answers.
    ///
    /// The actual fix is the same rule as everywhere else in this file: newest wins, and
    /// "newest" is judged by when the money moved, not by which batch happened to arrive
    /// last. ApplyBalanceIfNewerAsync only accepts this figure if nothing fresher has already
    /// been applied.
    /// </summary>
    private async Task RecordWalletTopUpAsync(SyncInboxEntry held, JsonElement root)
    {
        // The branch's own transaction id, so a redelivery is the same row rather than a second
        // one. Without it the same Rs 1,000 could be counted twice in a monthly total.
        var txId = ReadGuid(root, "walletTransactionId") ?? held.AggregateId;
        if (await _db.WalletTransactions.AnyAsync(w => w.Id == txId))
            return;

        var memberId = ReadGuid(root, "memberId") ?? held.AggregateId;
        var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == memberId)
            ?? throw new InvalidOperationException(
                $"Head Office has no member {memberId}. The member.created event should arrive first.");

        var cash = ReadDecimal(root, "cashAmount") ?? 0m;
        var online = ReadDecimal(root, "onlineAmount") ?? 0m;
        var bonus = ReadDecimal(root, "bonusAmount") ?? 0m;
        var credited = ReadDecimal(root, "totalCredit") ?? (cash + bonus);
        var gamingAfter = ReadDecimal(root, "gamingBalanceAfter") ?? 0m;
        var foodAfter = ReadDecimal(root, "foodBalanceAfter");
        var occurredAt = ReadDate(root, "occurredAt") ?? held.OccurredAt;

        _db.WalletTransactions.Add(new WalletTransaction
        {
            Id = txId,
            MemberId = memberId,
            BranchId = held.BranchId,
            OperatorId = await KnownHereOnly<Operator>(ReadGuid(root, "operatorId")),
            ShiftId = await KnownHereOnly<Shift>(ReadGuid(root, "shiftId")),
            Action = WalletAction.Recharge,
            TargetWallet = WalletType.Gaming,
            Amount = credited,
            BalanceBefore = gamingAfter - credited,
            BalanceAfter = gamingAfter,
            PaymentType = ReadString(root, "paymentType"),
            CashAmount = cash,
            OnlineAmount = online,
            BonusAmount = bonus,
            CreatedAt = occurredAt,
        });

        ApplyBalanceIfNewer(member, gamingAfter, foodAfter, occurredAt);
    }

    /// <summary>
    /// A member spending their own wallet at the counter, recorded at Head Office.
    ///
    /// The far more common half of wallet activity, and until now the branch never sent it at
    /// all - top-ups travelled up, spending did not, so Head Office's figure could only climb.
    /// A confident number that only ever goes up is worse than an honestly stale one: it looks
    /// correct right up until it lets a member spend a balance twice at two different shops.
    /// </summary>
    private async Task RecordWalletDeductionAsync(SyncInboxEntry held, JsonElement root)
    {
        var txId = ReadGuid(root, "walletTransactionId") ?? held.AggregateId;
        if (await _db.WalletTransactions.AnyAsync(w => w.Id == txId))
            return;

        var memberId = ReadGuid(root, "memberId") ?? held.AggregateId;
        var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == memberId)
            ?? throw new InvalidOperationException(
                $"Head Office has no member {memberId}. The member.created event should arrive first.");

        var amount = ReadDecimal(root, "amount") ?? 0m;
        var targetWalletRaw = ReadString(root, "targetWallet") ?? "Gaming";
        var isGaming = string.Equals(targetWalletRaw, "Gaming", StringComparison.OrdinalIgnoreCase);
        var gamingAfter = ReadDecimal(root, "gamingBalanceAfter") ?? member.GamingBalance;
        var foodAfter = ReadDecimal(root, "foodBalanceAfter") ?? member.FoodBalance;
        var occurredAt = ReadDate(root, "occurredAt") ?? held.OccurredAt;

        _db.WalletTransactions.Add(new WalletTransaction
        {
            Id = txId,
            MemberId = memberId,
            BranchId = held.BranchId,
            OperatorId = await KnownHereOnly<Operator>(ReadGuid(root, "operatorId")),
            ShiftId = await KnownHereOnly<Shift>(ReadGuid(root, "shiftId")),
            Action = WalletAction.Correction,
            TargetWallet = isGaming ? WalletType.Gaming : WalletType.Food,
            Amount = amount,
            BalanceBefore = (isGaming ? gamingAfter : foodAfter) + amount,
            BalanceAfter = isGaming ? gamingAfter : foodAfter,
            PaymentType = "Wallet",
            BillId = await KnownHereOnly<Bill>(ReadGuid(root, "billId")),
            Reason = ReadString(root, "reason"),
            CreatedAt = occurredAt,
        });

        ApplyBalanceIfNewer(member, gamingAfter, foodAfter, occurredAt);
    }

    /// <summary>
    /// The one rule that keeps a wallet correct across four branches touching it independently:
    /// the figure that happened most recently wins, never the one that merely arrived most
    /// recently.
    ///
    /// Without this, a branch's sync batch delayed by a slow connection could land after a
    /// newer one from a different branch and quietly drag the balance backwards - undoing a
    /// spend nobody undid, or resurrecting money that was already spent elsewhere.
    /// </summary>
    /// <summary>A free bonus given at the branch (WalletService.GiveBonusAsync). Stored as
    /// WalletAction.Bonus with no cash/online, so Head Office's EOD and reports - which count
    /// Recharge only - never treat it as money received.</summary>
    private async Task RecordWalletBonusAsync(SyncInboxEntry held, JsonElement root)
    {
        var txId = ReadGuid(root, "walletTransactionId") ?? held.Id;
        if (await _db.WalletTransactions.AnyAsync(w => w.Id == txId))
            return;

        var memberId = ReadGuid(root, "memberId") ?? held.AggregateId;
        var member = await _db.Members.FirstOrDefaultAsync(m => m.Id == memberId)
            ?? throw new InvalidOperationException(
                $"Head Office has no member {memberId}. The member.created event should arrive first.");

        var amount = ReadDecimal(root, "amount") ?? 0m;
        var isGaming = !string.Equals(ReadString(root, "targetWallet"), "Food", StringComparison.OrdinalIgnoreCase);
        var gamingAfter = ReadDecimal(root, "gamingBalanceAfter") ?? member.GamingBalance;
        var foodAfter = ReadDecimal(root, "foodBalanceAfter") ?? member.FoodBalance;
        var balanceAfter = isGaming ? gamingAfter : foodAfter;
        var occurredAt = ReadDate(root, "occurredAt") ?? held.OccurredAt;

        _db.WalletTransactions.Add(new WalletTransaction
        {
            Id = txId,
            MemberId = memberId,
            BranchId = held.BranchId,
            RemoteAdminId = ReadGuid(root, "actorId"),
            RemoteAdminName = ReadString(root, "actorName"),
            Action = WalletAction.Bonus,
            TargetWallet = isGaming ? WalletType.Gaming : WalletType.Food,
            Amount = amount,
            BalanceBefore = balanceAfter - amount,
            BalanceAfter = balanceAfter,
            PaymentType = "Bonus",
            CashAmount = 0,
            OnlineAmount = 0,
            BonusAmount = amount,
            Reason = ReadString(root, "reason"),
            CreatedAt = occurredAt,
        });

        if (isGaming) member.TotalGamingBonusEarned += amount;
        ApplyBalanceIfNewer(member, gamingAfter, foodAfter, occurredAt);
    }

    private static void ApplyBalanceIfNewer(Member member, decimal gamingAfter, decimal? foodAfter, DateTimeOffset occurredAt)
    {
        if (member.BalanceAsOf is { } asOf && asOf >= occurredAt) return;

        member.GamingBalance = gamingAfter;
        if (foodAfter is { } f) member.FoodBalance = f;
        member.BalanceAsOf = occurredAt;
        member.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task UpsertSessionStartedAsync(SyncInboxEntry held, JsonElement root)
    {
        var sessionId = held.AggregateId;
        if (await _db.Sessions.AnyAsync(s => s.Id == sessionId))
            return;   // already known: a redelivery, or this box is both branch and Head Office

        var pcId = ReadGuid(root, "pcId");
        var operatorId = ReadGuid(root, "operatorId");

        // Head Office can only hold a session pointing at a PC and operator it knows about.
        // Refusing loudly is right — a silently orphaned session would quietly skew reports.
        if (pcId is null || !await _db.Pcs.AnyAsync(p => p.Id == pcId))
            throw new InvalidOperationException(
                $"Head Office has no PC {pcId} for branch {held.BranchId}. " +
                "Provision the branch from Head Office so both sides share identifiers.");

        if (operatorId is null || !await _db.Operators.AnyAsync(o => o.Id == operatorId))
            throw new InvalidOperationException($"Head Office has no operator {operatorId}.");

        var startTime = ReadDate(root, "startTime") ?? held.OccurredAt;
        var plannedMin = ReadInt(root, "plannedDurationMin");

        _db.Sessions.Add(new Session
        {
            Id = sessionId,
            BranchId = held.BranchId,
            PcId = pcId.Value,
            OperatorId = operatorId.Value,
            ShiftId = await KnownHereOnly<Shift>(ReadGuid(root, "shiftId")),
            MemberId = await KnownHereOnly<Member>(ReadGuid(root, "memberId")),
            CustomerName = ReadString(root, "customerName"),
            StartTime = startTime,
            PlannedDurationMin = plannedMin,

            // When the session was sold with a length, work out when it is due to end.
            //
            // This is why Head Office showed an hour's play as unlimited. The duration arrived
            // and was stored faithfully, but EndTime was left null - and the PC grid decides
            // pay-as-you-go purely by "has no end time". So every synced session, however it
            // was sold, drew the infinity symbol and no countdown. The branch's own screen was
            // right the whole time, which is what made the two impossible to reconcile.
            //
            // Computed rather than sent, because the branch already sends both halves and a
            // number derived twice from the same two values cannot disagree with itself.
            // Genuine pay-as-you-go has no planned duration and correctly stays null here.
            EndTime = plannedMin is > 0 ? startTime.AddMinutes(plannedMin.Value) : null,

            GamingType = ReadString(root, "gamingType") ?? "standard",
            TotalAmount = ReadDecimal(root, "expectedAmount") ?? 0m,
            GamingAmount = ReadDecimal(root, "expectedAmount") ?? 0m,
            State = SessionState.Active,
            CreatedAt = held.OccurredAt,
            UpdatedAt = held.ReceivedAt,
        });

        // PC state is deliberately NOT written here. See UpsertSessionStoppedAsync below.
    }

    private async Task UpsertSessionStoppedAsync(SyncInboxEntry held, JsonElement root)
    {
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == held.AggregateId);

        if (session is null)
        {
            // A stop can arrive without its start if the branch was offline when the session
            // began and the backlog was split across batches. The stop event carries enough
            // to rebuild the whole thing.
            await UpsertSessionStartedAsync(held, root);
            await _db.SaveChangesAsync();
            session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == held.AggregateId);
            if (session is null) return;
        }

        session.State = SessionState.Completed;
        session.EndTime = ReadDate(root, "endTime");
        session.ActualDurationMin = ReadInt(root, "billedMinutes");
        session.PausedSeconds = ReadInt(root, "pausedSeconds") ?? 0;
        session.GamingAmount = ReadDecimal(root, "gamingAmount") ?? session.GamingAmount;
        session.FoodAmount = ReadDecimal(root, "foodAmount") ?? session.FoodAmount;
        session.TotalAmount = ReadDecimal(root, "totalAmount") ?? session.TotalAmount;
        session.UpdatedAt = held.ReceivedAt;

        // PC state is deliberately not written from events, and this is the fix for a grid
        // that was right most of the time and wrong the rest.
        //
        // Two things were writing pcs.State. Events arrive in batches and can be up to the
        // courier's whole interval old; the heartbeat arrives every three seconds and is always
        // current. So a session.stopped delivered late would set a PC back to idle after the
        // heartbeat had already reported the next customer sitting at it - and thirty seconds
        // later the heartbeat would put it back. A PC flickering between two states, each
        // writer perfectly correct about a different moment.
        //
        // No amount of speed fixes that; it only narrows the window. The rule that does fix it
        // is one owner per fact:
        //
        //   state   - what a PC is doing NOW, who is on shift, what the drawer holds.
        //             Owned by the heartbeat. Only the newest matters, so the newest wins.
        //
        //   history - a session happened, a bill was paid, Rs 180 was taken.
        //             Owned by these events. Must never be lost, so they queue and retry.
        //
        // This method still records the session's own history - its end time, its minutes, its
        // money - because that is history and belongs here. Where the PC stands right now is
        // the branch's to report, and it reports it three seconds from now regardless.
    }

    /// <summary>
    /// Builds Head Office's record of a food order from scratch, items and all - the part that
    /// never existed before. A walk-in order's total happened to arrive anyway, because it
    /// creates a Bill and Bill is separately watched; a session-linked order updated nothing
    /// synced until the food was later marked delivered, and even then only a total ever
    /// crossed - never which dishes, how many, or that an order existed at all while it was
    /// still being cooked.
    /// </summary>
    private async Task UpsertFoodOrderPlacedAsync(SyncInboxEntry held, JsonElement root)
    {
        var orderId = held.AggregateId;
        if (await _db.Set<FoodOrder>().AnyAsync(o => o.Id == orderId))
            return;   // already known: a redelivery

        var order = new FoodOrder
        {
            Id = orderId,
            OrderNumber = ReadString(root, "orderNumber") ?? orderId.ToString("N")[..8],
            BranchId = held.BranchId,
            SessionId = await KnownHereOnly<Session>(ReadGuid(root, "sessionId")),
            PcId = await KnownHereOnly<Pc>(ReadGuid(root, "pcId")),
            BillId = await KnownHereOnly<Bill>(ReadGuid(root, "billId")),
            OperatorId = await KnownHereOnly<Operator>(ReadGuid(root, "operatorId")),
            MemberId = await KnownHereOnly<Member>(ReadGuid(root, "memberId")),
            CustomerName = ReadString(root, "customerName"),
            PaymentType = ReadString(root, "paymentType"),
            TotalAmount = ReadDecimal(root, "totalAmount") ?? 0m,
            Status = OrderStatus.Pending,
            OrderTime = ReadDate(root, "orderTime") ?? held.OccurredAt,
            CreatedAt = held.OccurredAt,
            UpdatedAt = held.ReceivedAt,
        };

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var inventoryId = ReadGuid(item, "inventoryId");

                // Unlike the FKs above, this one is not nullable on the row itself - a food
                // order line with no product is not a real line. Left unresolved (not silently
                // dropped), so the whole entry is marked unapplied and retried once the menu
                // item's own inventory_item.changed event has landed, rather than arriving here
                // as a plate of food nobody can identify.
                if (inventoryId is null || !await _db.Set<InventoryItem>().AnyAsync(i => i.Id == inventoryId))
                    throw new InvalidOperationException(
                        $"Head Office does not yet know menu item {inventoryId} for order {orderId}.");

                order.Items.Add(new FoodOrderItem
                {
                    InventoryId = inventoryId.Value,
                    ItemName = ReadString(item, "itemName") ?? "Item",
                    Quantity = ReadInt(item, "quantity") ?? 1,
                    UnitPrice = ReadDecimal(item, "unitPrice") ?? 0m,
                    TotalPrice = ReadDecimal(item, "totalPrice") ?? 0m,
                    CreatedAt = held.OccurredAt,
                });
            }
        }

        _db.Add(order);
    }

    /// <summary>
    /// Moves Head Office's copy of an order through the same states the kitchen actually moved
    /// it through - accepted, ready, delivered, cancelled and why - none of which reached here
    /// before at all.
    /// </summary>
    private async Task ApplyFoodOrderStatusAsync(SyncInboxEntry held, JsonElement root)
    {
        var order = await _db.Set<FoodOrder>().FirstOrDefaultAsync(o => o.Id == held.AggregateId);

        // The order's own "placed" event should always arrive first - it is written first, at
        // the branch, in the same outbox that delivers in that order. If it has not yet, this
        // entry is marked unapplied and retried once it has, rather than guessed at: a status
        // change carries none of what was actually ordered, so there is nothing safe to
        // reconstruct from it alone.
        if (order is null)
            throw new InvalidOperationException(
                $"Head Office has no food order {held.AggregateId} yet to apply this status change to.");

        var status = ReadString(root, "status");
        if (status is not null && Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed))
            order.Status = parsed;

        order.CancelledReason = ReadString(root, "reason");
        order.AcceptedAt = ReadDate(root, "acceptedAt");
        order.ReadyAt = ReadDate(root, "readyAt");
        order.DeliveredAt = ReadDate(root, "deliveredAt");
        order.CompletedAt = ReadDate(root, "completedAt");
        order.TotalAmount = ReadDecimal(root, "totalAmount") ?? order.TotalAmount;
        order.UpdatedAt = held.ReceivedAt;
    }

    // ── Payload readers ──
    // Tolerant on purpose: a branch running a slightly older build must not render a whole
    // batch unprocessable, so a missing or malformed field simply reads as null.

    private static Guid? ReadGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && Guid.TryParse(v.GetString(), out var g) ? g : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetInt32(out var i) ? i : null;

    private static decimal? ReadDecimal(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
        && v.TryGetDecimal(out var d) ? d : null;

    // Always returns UTC. Branch payloads carry local offsets (+05:30), and every one of
    // these values ends up in a "timestamp with time zone" column that accepts only UTC.
    private static DateTimeOffset? ReadDate(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var t) ? t.ToUniversalTime() : null;
}

public class ReceiveSyncBatchDto
{
    public Guid BranchId { get; set; }
    public List<SyncEntryDto> Entries { get; set; } = new();
}

public class SyncEntryDto
{
    public Guid Id { get; set; }
    public string? AggregateType { get; set; }
    public Guid AggregateId { get; set; }
    public string? EventType { get; set; }
    public object? EventData { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ReconcileManifestDto
{
    public Guid BranchId { get; set; }
    public List<ReconcileManifestEntryDto> Entries { get; set; } = new();
}

public class ReconcileManifestEntryDto
{
    public string? AggregateType { get; set; }
    public Guid AggregateId { get; set; }
    public string? Checksum { get; set; }
}

public class ReconcileResultDto
{
    public List<ReconcileMismatchDto> Mismatched { get; set; } = new();
}

public class ReconcileMismatchDto
{
    public string AggregateType { get; set; } = "";
    public Guid AggregateId { get; set; }
    public string Reason { get; set; } = "";
}

public class ParitySnapshotDto
{
    public int TotalPcs { get; set; }
    public int PcsActive { get; set; }
    public int PcsAwaitingBilling { get; set; }
    public int PcsIdle { get; set; }
    public int PcsMaintenance { get; set; }
    public int OperatorsOnDuty { get; set; }
    public decimal EodNetRevenue { get; set; }
    public decimal EodGamingRevenue { get; set; }
    public decimal EodFoodRevenue { get; set; }
    public decimal EodCashTotal { get; set; }
    public decimal EodOnlineTotal { get; set; }
    public decimal ExpectedDrawerCash { get; set; }
}
